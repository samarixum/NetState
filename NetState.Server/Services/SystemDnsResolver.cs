using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NetState.Shared.Core;

namespace NetState.Server.Services;

/* :: :: SystemDnsResolver :: START :: */

/// <summary>
/// Cross-platform DNS resolver supporting native Windows <c>dnsapi.dll</c> cache-bypassed queries and
/// RFC 1035 UDP/TCP wire queries for custom DNS servers or non-Windows platforms.
/// </summary>
public sealed class SystemDnsResolver : IDnsResolver {
    private const uint DnsQueryBypassCache = 0x00000008;

    private static readonly Dictionary<string, ushort> TypeMap = new(StringComparer.OrdinalIgnoreCase) {
        ["A"] = 1,
        ["NS"] = 2,
        ["CNAME"] = 5,
        ["SOA"] = 6,
        ["PTR"] = 12,
        ["MX"] = 15,
        ["TXT"] = 16,
        ["RP"] = 17,
        ["AFSDB"] = 18,
        ["AAAA"] = 28,
        ["LOC"] = 29,
        ["SRV"] = 33,
        ["NAPTR"] = 35,
        ["KX"] = 36,
        ["CERT"] = 37,
        ["DNAME"] = 39,
        ["DS"] = 43,
        ["SSHFP"] = 44,
        ["IPSECKEY"] = 45,
        ["RRSIG"] = 46,
        ["NSEC"] = 47,
        ["DNSKEY"] = 48,
        ["DHCID"] = 49,
        ["TLSA"] = 52,
        ["SMIMEA"] = 53,
        ["CDS"] = 59,
        ["CDNSKEY"] = 60,
        ["OPENPGPKEY"] = 61,
        ["CSYNC"] = 62,
        ["ZONEMD"] = 63,
        ["SVCB"] = 64,
        ["HTTPS"] = 65,
        ["SPF"] = 99,
        ["URI"] = 256,
        ["CAA"] = 257,
        ["ANY"] = 255
    };

    private static readonly Dictionary<ushort, string> ReverseTypeMap = TypeMap
        .GroupBy(kvp => kvp.Value)
        .ToDictionary(g => g.Key, g => g.First().Key);

    /* :: :: P/Invoke Declarations :: START :: */

    [DllImport("dnsapi.dll", EntryPoint = "DnsQuery_W", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int DnsQueryW(
        string pszName,
        ushort wType,
        uint options,
        IntPtr pExtra,
        out IntPtr ppQueryResults,
        IntPtr pReserved
    );

    [DllImport("dnsapi.dll", CharSet = CharSet.Unicode)]
    private static extern void DnsRecordListFree(IntPtr pRecordList, int freeType);

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowsDnsRecordHeader {
        public IntPtr Next;
        public IntPtr Name;
        public ushort Type;
        public ushort DataLength;
        public uint Flags;
        public uint Ttl;
        public uint Reserved;
    }

    /* :: :: P/Invoke Declarations :: END :: */
    // //
    /* :: :: Public Methods :: START :: */

    /// <summary>
    /// Converts a DNS record type string (e.g. <c>TXT</c>, <c>CAA</c>, <c>TYPE65</c>, or <c>257</c>) to its IANA type code.
    /// </summary>
    /// <param name="recordType">The record type string.</param>
    /// <returns>The numeric DNS record type code.</returns>
    public static ushort ParseRecordTypeCode(string? recordType) {
        if (string.IsNullOrWhiteSpace(recordType)) {
            return 1;
        }

        string clean = recordType.Trim().ToUpperInvariant();
        if (TypeMap.TryGetValue(clean, out ushort mapped)) {
            return mapped;
        }

        if (clean.StartsWith("TYPE", StringComparison.Ordinal) && ushort.TryParse(clean[4..], NumberStyles.Integer, CultureInfo.InvariantCulture, out ushort typeNum)) {
            return typeNum;
        }

        if (ushort.TryParse(clean, NumberStyles.Integer, CultureInfo.InvariantCulture, out ushort rawNum)) {
            return rawNum;
        }

        throw new ArgumentException($"Unsupported DNS record type '{recordType}'. Use standard names (A, AAAA, CNAME, MX, TXT, SRV, NS, SOA, CAA, HTTPS, SVCB, ANY) or TYPE<number>.", nameof(recordType));
    }

    /// <summary>
    /// Converts a numeric DNS record type code to its canonical string name.
    /// </summary>
    /// <param name="typeCode">The IANA DNS type code.</param>
    /// <returns>The canonical type name or <c>TYPE&lt;n&gt;</c>.</returns>
    public static string FormatRecordTypeName(ushort typeCode) {
        if (ReverseTypeMap.TryGetValue(typeCode, out string? name)) {
            return name;
        }

        return $"TYPE{typeCode}";
    }

    /// <inheritdoc />
    public async Task<DnsQueryOutcome> QueryAsync(
        string host,
        string recordType,
        string? dnsServer = null,
        bool bypassCache = true,
        CancellationToken ct = default
    ) {
        string cleanHost = DomainGroupingHelper.ExtractHost(host);
        if (string.IsNullOrWhiteSpace(cleanHost)) {
            return new DnsQueryOutcome(host ?? string.Empty, recordType ?? "A", "None", false, "ERROR", new(), new(), "Hostname cannot be empty.");
        }

        ushort typeCode;
        string canonicalType;
        try {
            typeCode = ParseRecordTypeCode(recordType);
            canonicalType = FormatRecordTypeName(typeCode);
        } catch (Exception ex) {
            return new DnsQueryOutcome(cleanHost, recordType ?? string.Empty, "None", false, "ERROR", new(), new(), ex.Message);
        }

        if (string.IsNullOrWhiteSpace(dnsServer) && OperatingSystem.IsWindows()) {
            return await Task.Run(() => QueryWindowsNative(cleanHost, typeCode, canonicalType, bypassCache), ct);
        }

        return await QueryWireAsync(cleanHost, typeCode, canonicalType, dnsServer, ct);
    }

    /* :: :: Public Methods :: END :: */
    // //
    /* :: :: Windows Native DNS Implementation :: START :: */

    private static DnsQueryOutcome QueryWindowsNative(
        string cleanHost,
        ushort typeCode,
        string canonicalType,
        bool bypassCache
    ) {
        uint options = bypassCache ? DnsQueryBypassCache : 0u;
        IntPtr pResults = IntPtr.Zero;
        int winStatus = DnsQueryW(cleanHost, typeCode, options, IntPtr.Zero, out pResults, IntPtr.Zero);

        List<DnsRecordEntry> matchedRecords = new();
        List<string> cnameChain = new();

        try {
            IntPtr current = pResults;
            while (current != IntPtr.Zero) {
                WindowsDnsRecordHeader header = Marshal.PtrToStructure<WindowsDnsRecordHeader>(current);
                uint sectionVal = header.Flags & 0x3u;
                string section = sectionVal switch {
                    0 => "Question",
                    1 => "Answer",
                    2 => "Authority",
                    3 => "Additional",
                    _ => sectionVal.ToString(CultureInfo.InvariantCulture)
                };

                string recordName = DomainGroupingHelper.NormalizeDnsName(Marshal.PtrToStringUni(header.Name));
                string recTypeName = FormatRecordTypeName(header.Type);
                IntPtr dataPtr = current + Marshal.SizeOf<WindowsDnsRecordHeader>();
                (string value, byte[] rawBytes) = ParseWindowsRecordData(header.Type, header.DataLength, dataPtr);

                if (sectionVal == 1) {
                    if (header.Type == 5 && typeCode != 5 && !string.IsNullOrEmpty(value)) {
                        cnameChain.Add(value);
                    }

                    if (typeCode == 255 || header.Type == typeCode) {
                        matchedRecords.Add(new DnsRecordEntry(
                            Name: recordName,
                            Type: recTypeName,
                            Section: section,
                            Ttl: header.Ttl,
                            Value: value,
                            RawData: rawBytes
                        ));
                    }
                }

                current = header.Next;
            }
        } finally {
            if (pResults != IntPtr.Zero) {
                DnsRecordListFree(pResults, 1);
            }
        }

        string statusCode;
        bool success;
        string? errorMsg = null;

        switch (winStatus) {
            case 0: {
                if (matchedRecords.Count > 0) {
                    statusCode = "OK";
                } else {
                    statusCode = "NO_RECORDS";
                }
                success = true;
                break;
            }
            case 9003: // DNS_ERROR_RCODE_NAME_ERROR
                statusCode = "NXDOMAIN";
                success = true;
                break;
            case 9501: // DNS_INFO_NO_RECORDS
                statusCode = "NO_RECORDS";
                success = true;
                break;
            default: {
                statusCode = "ERROR";
                success = false;
                errorMsg = $"Windows DNS query failed with Win32 error {winStatus}.";
                break;
            }
        }

        return new DnsQueryOutcome(
            QueryHost: cleanHost,
            QueryType: canonicalType,
            ResolverUsed: "WindowsDnsApi",
            Success: success,
            StatusCode: statusCode,
            Records: matchedRecords,
            CnameChain: cnameChain,
            ErrorMessage: errorMsg
        );
    }

    private static (string Value, byte[] RawBytes) ParseWindowsRecordData(ushort typeCode, ushort dataLen, IntPtr dataPtr) {
        switch (typeCode) {
            case 1: { // A
                byte[] ipBytes = new byte[4];
                Marshal.Copy(dataPtr, ipBytes, 0, 4);
                return (new IPAddress(ipBytes).ToString(), ipBytes);
            }
            case 28: { // AAAA
                byte[] ipBytes = new byte[16];
                Marshal.Copy(dataPtr, ipBytes, 0, 16);
                return (new IPAddress(ipBytes).ToString(), ipBytes);
            }
            case 2: // NS
            case 5: // CNAME
            case 12: // PTR
            case 39: { // DNAME
                IntPtr pName = Marshal.ReadIntPtr(dataPtr);
                string val = DomainGroupingHelper.NormalizeDnsName(Marshal.PtrToStringUni(pName));
                return (val, Array.Empty<byte>());
            }
            case 15: { // MX
                IntPtr pExchange = Marshal.ReadIntPtr(dataPtr);
                ushort preference = (ushort)Marshal.ReadInt16(dataPtr + IntPtr.Size);
                string exchange = DomainGroupingHelper.NormalizeDnsName(Marshal.PtrToStringUni(pExchange));
                return ($"{preference} {exchange}", Array.Empty<byte>());
            }
            case 16: // TXT
            case 99: { // SPF
                uint count = (uint)Marshal.ReadInt32(dataPtr);
                int arrOffset = IntPtr.Size;
                StringBuilder sb = new();
                for (int i = 0; i < count; i++) {
                    IntPtr pStr = Marshal.ReadIntPtr(dataPtr + arrOffset + (i * IntPtr.Size));
                    if (pStr != IntPtr.Zero) {
                        sb.Append(Marshal.PtrToStringUni(pStr));
                    }
                }
                return (sb.ToString(), Array.Empty<byte>());
            }
            case 33: { // SRV
                IntPtr pTarget = Marshal.ReadIntPtr(dataPtr);
                int off = IntPtr.Size;
                ushort prio = (ushort)Marshal.ReadInt16(dataPtr + off);
                ushort weight = (ushort)Marshal.ReadInt16(dataPtr + off + 2);
                ushort port = (ushort)Marshal.ReadInt16(dataPtr + off + 4);
                string target = DomainGroupingHelper.NormalizeDnsName(Marshal.PtrToStringUni(pTarget));
                return ($"{prio} {weight} {port} {target}", Array.Empty<byte>());
            }
            case 6: { // SOA
                IntPtr pPrimary = Marshal.ReadIntPtr(dataPtr);
                IntPtr pAdmin = Marshal.ReadIntPtr(dataPtr + IntPtr.Size);
                int intBase = IntPtr.Size * 2;
                uint serial = (uint)Marshal.ReadInt32(dataPtr + intBase);
                uint refresh = (uint)Marshal.ReadInt32(dataPtr + intBase + 4);
                uint retry = (uint)Marshal.ReadInt32(dataPtr + intBase + 8);
                uint expire = (uint)Marshal.ReadInt32(dataPtr + intBase + 12);
                uint minTtl = (uint)Marshal.ReadInt32(dataPtr + intBase + 16);
                string primary = DomainGroupingHelper.NormalizeDnsName(Marshal.PtrToStringUni(pPrimary));
                string admin = DomainGroupingHelper.NormalizeDnsName(Marshal.PtrToStringUni(pAdmin));
                return ($"{primary} {admin} {serial} {refresh} {retry} {expire} {minTtl}", Array.Empty<byte>());
            }
            default: {
                byte[] raw = dataLen > 0 ? new byte[dataLen] : Array.Empty<byte>();
                if (dataLen > 0) {
                    Marshal.Copy(dataPtr, raw, 0, dataLen);
                }

                string parsed = ParseWireRdata(typeCode, raw, fullPacket: null, rdataOffset: 0);
                return (parsed, raw);
            }
        }
    }

    /* :: :: Windows Native DNS Implementation :: END :: */
    // //
    /* :: :: RFC 1035 Wire DNS Implementation :: START :: */

    private static async Task<DnsQueryOutcome> QueryWireAsync(
        string cleanHost,
        ushort typeCode,
        string canonicalType,
        string? dnsServer,
        CancellationToken ct
    ) {
        IPEndPoint endpoint;
        try {
            endpoint = ResolveServerEndPoint(dnsServer);
        } catch (Exception ex) {
            return new DnsQueryOutcome(cleanHost, canonicalType, dnsServer ?? "DefaultWire", false, "ERROR", new(), new(), ex.Message);
        }

        string resolverLabel = $"Wire:{endpoint}";
        try {
            ushort txId = (ushort)RandomNumberGenerator.GetInt32(1, 65536);
            byte[] queryPacket = BuildDnsQueryPacket(txId, cleanHost, typeCode);

            using CancellationTokenSource timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(5));

            byte[] responsePacket = await SendUdpWithTcpFallbackAsync(endpoint, queryPacket, timeoutCts.Token);
            return ParseWireResponsePacket(cleanHost, typeCode, canonicalType, resolverLabel, txId, responsePacket);
        } catch (OperationCanceledException) when (!ct.IsCancellationRequested) {
            return new DnsQueryOutcome(cleanHost, canonicalType, resolverLabel, false, "ERROR", new(), new(), $"DNS wire query to {endpoint} timed out.");
        } catch (Exception ex) {
            return new DnsQueryOutcome(cleanHost, canonicalType, resolverLabel, false, "ERROR", new(), new(), $"DNS wire query to {endpoint} failed: {ex.Message}");
        }
    }

    private static IPEndPoint ResolveServerEndPoint(string? dnsServer) {
        if (!string.IsNullOrWhiteSpace(dnsServer)) {
            string trimmed = dnsServer.Trim();
            if (IPEndPoint.TryParse(trimmed, out IPEndPoint? parsedEp)) {
                return new IPEndPoint(parsedEp.Address, parsedEp.Port == 0 ? 53 : parsedEp.Port);
            }

            if (IPAddress.TryParse(trimmed, out IPAddress? ip)) {
                return new IPEndPoint(ip, 53);
            }

            throw new ArgumentException($"Invalid DNS server address '{dnsServer}'. Expected an IP address or IP:port.");
        }

        foreach (NetworkInterface nic in NetworkInterface.GetAllNetworkInterfaces()) {
            if (nic.OperationalStatus != OperationalStatus.Up) {
                continue;
            }

            foreach (IPAddress addr in nic.GetIPProperties().DnsAddresses) {
                if (addr.AddressFamily == AddressFamily.InterNetwork || addr.AddressFamily == AddressFamily.InterNetworkV6) {
                    return new IPEndPoint(addr, 53);
                }
            }
        }

        return new IPEndPoint(IPAddress.Parse("1.1.1.1"), 53);
    }

    private static byte[] BuildDnsQueryPacket(ushort txId, string host, ushort typeCode) {
        using MemoryStream ms = new();
        Span<byte> header = stackalloc byte[12];
        BinaryPrimitives.WriteUInt16BigEndian(header[0..2], txId);
        BinaryPrimitives.WriteUInt16BigEndian(header[2..4], 0x0100); // Standard query, Recursion Desired
        BinaryPrimitives.WriteUInt16BigEndian(header[4..6], 1); // QDCOUNT
        BinaryPrimitives.WriteUInt16BigEndian(header[6..8], 0);
        BinaryPrimitives.WriteUInt16BigEndian(header[8..10], 0);
        BinaryPrimitives.WriteUInt16BigEndian(header[10..12], 1); // ARCOUNT = 1 (EDNS0)
        ms.Write(header);

        foreach (string label in host.Split('.', StringSplitOptions.RemoveEmptyEntries)) {
            byte[] labelBytes = Encoding.ASCII.GetBytes(label);
            if (labelBytes.Length > 63) {
                throw new ArgumentException($"DNS label '{label}' exceeds 63 bytes.");
            }
            ms.WriteByte((byte)labelBytes.Length);
            ms.Write(labelBytes);
        }
        ms.WriteByte(0);

        Span<byte> qTail = stackalloc byte[4];
        BinaryPrimitives.WriteUInt16BigEndian(qTail[0..2], typeCode);
        BinaryPrimitives.WriteUInt16BigEndian(qTail[2..4], 1); // IN
        ms.Write(qTail);

        // OPT pseudo-RR for 4096 UDP payload size
        ms.WriteByte(0); // Root
        Span<byte> opt = stackalloc byte[10];
        BinaryPrimitives.WriteUInt16BigEndian(opt[0..2], 41); // OPT
        BinaryPrimitives.WriteUInt16BigEndian(opt[2..4], 4096); // UDP payload
        opt[4..10].Clear();
        ms.Write(opt);

        return ms.ToArray();
    }

    private static async Task<byte[]> SendUdpWithTcpFallbackAsync(
        IPEndPoint endpoint,
        byte[] queryPacket,
        CancellationToken ct
    ) {
        using UdpClient udp = new(endpoint.AddressFamily);
        await udp.SendAsync(queryPacket, endpoint, ct);
        UdpReceiveResult udpResult = await udp.ReceiveAsync(ct);
        byte[] buffer = udpResult.Buffer;

        if (buffer.Length >= 4 && (buffer[2] & 0x02) != 0) {
            // TC (Truncated) bit set -> retry over TCP
            using TcpClient tcp = new(endpoint.AddressFamily);
            await tcp.ConnectAsync(endpoint.Address, endpoint.Port, ct);
            await using NetworkStream stream = tcp.GetStream();

            byte[] lengthPrefix = new byte[2];
            BinaryPrimitives.WriteUInt16BigEndian(lengthPrefix, (ushort)queryPacket.Length);
            await stream.WriteAsync(lengthPrefix, ct);
            await stream.WriteAsync(queryPacket, ct);
            await stream.FlushAsync(ct);

            byte[] respLenBuf = new byte[2];
            await ReadExactAsync(stream, respLenBuf, ct);
            int respLen = BinaryPrimitives.ReadUInt16BigEndian(respLenBuf);
            byte[] tcpResponse = new byte[respLen];
            await ReadExactAsync(stream, tcpResponse, ct);
            return tcpResponse;
        }

        return buffer;
    }

    private static async Task ReadExactAsync(Stream stream, byte[] buffer, CancellationToken ct) {
        int totalRead = 0;
        while (totalRead < buffer.Length) {
            int read = await stream.ReadAsync(buffer.AsMemory(totalRead, buffer.Length - totalRead), ct);
            if (read == 0) {
                throw new EndOfStreamException("Unexpected end of TCP DNS stream.");
            }
            totalRead += read;
        }
    }

    private static DnsQueryOutcome ParseWireResponsePacket(
        string cleanHost,
        ushort requestedTypeCode,
        string canonicalType,
        string resolverLabel,
        ushort expectedTxId,
        byte[] packet
    ) {
        if (packet.Length < 12) {
            return new DnsQueryOutcome(cleanHost, canonicalType, resolverLabel, false, "ERROR", new(), new(), "Malformed DNS response (<12 bytes).");
        }

        ushort txId = BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(0, 2));
        if (txId != expectedTxId) {
            return new DnsQueryOutcome(cleanHost, canonicalType, resolverLabel, false, "ERROR", new(), new(), "DNS transaction ID mismatch.");
        }

        ushort flags = BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(2, 2));
        int rcode = flags & 0x0F;
        ushort qdCount = BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(4, 2));
        ushort anCount = BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(6, 2));

        int offset = 12;
        for (int i = 0; i < qdCount; i++) {
            _ = ReadWireName(packet, ref offset);
            offset += 4;
        }

        List<DnsRecordEntry> matched = new();
        List<string> cnameChain = new();

        for (int i = 0; i < anCount && offset < packet.Length; i++) {
            string recName = DomainGroupingHelper.NormalizeDnsName(ReadWireName(packet, ref offset));
            if (offset + 10 > packet.Length) {
                break;
            }

            ushort recType = BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(offset, 2));
            offset += 2; // TYPE
            offset += 2; // CLASS
            uint ttl = BinaryPrimitives.ReadUInt32BigEndian(packet.AsSpan(offset, 4));
            offset += 4;
            ushort rdLen = BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(offset, 2));
            offset += 2;

            if (offset + rdLen > packet.Length) {
                break;
            }

            int rdataOffset = offset;
            byte[] rawData = packet.AsSpan(rdataOffset, rdLen).ToArray();
            offset += rdLen;

            string parsedVal = ParseWireRdata(recType, rawData, packet, rdataOffset);

            if (recType == 5 && requestedTypeCode != 5 && !string.IsNullOrEmpty(parsedVal)) {
                cnameChain.Add(parsedVal);
            }

            if (requestedTypeCode == 255 || recType == requestedTypeCode) {
                matched.Add(new DnsRecordEntry(
                    Name: recName,
                    Type: FormatRecordTypeName(recType),
                    Section: "Answer",
                    Ttl: ttl,
                    Value: parsedVal,
                    RawData: rawData
                ));
            }
        }

        string statusCode;
        bool success;
        string? errorMsg = null;

        switch (rcode) {
            case 0: {
                statusCode = matched.Count > 0 ? "OK" : "NO_RECORDS";
                success = true;
                break;
            }
            case 3:
                statusCode = "NXDOMAIN";
                success = true;
                break;
            default: {
                statusCode = "ERROR";
                success = false;
                errorMsg = $"DNS server returned RCODE {rcode}.";
                break;
            }
        }

        return new DnsQueryOutcome(cleanHost, canonicalType, resolverLabel, success, statusCode, matched, cnameChain, errorMsg);
    }

    /// <summary>
    /// Decodes RDATA bytes from wire format for standard and miscellaneous DNS record types.
    /// </summary>
    internal static string ParseWireRdata(ushort typeCode, byte[] rawData, byte[]? fullPacket, int rdataOffset) {
        switch (typeCode) {
            case 1 when rawData.Length == 4: { // A
                return new IPAddress(rawData).ToString();
            }
            case 28 when rawData.Length == 16: { // AAAA
                return new IPAddress(rawData).ToString();
            }
            case 2: // NS
            case 5: // CNAME
            case 12: // PTR
            case 39: { // DNAME
                if (fullPacket != null) {
                    int pos = rdataOffset;
                    return DomainGroupingHelper.NormalizeDnsName(ReadWireName(fullPacket, ref pos));
                }
                break;
            }
            case 15 when rawData.Length >= 3: { // MX
                ushort pref = BinaryPrimitives.ReadUInt16BigEndian(rawData.AsSpan(0, 2));
                if (fullPacket != null) {
                    int pos = rdataOffset + 2;
                    string exch = DomainGroupingHelper.NormalizeDnsName(ReadWireName(fullPacket, ref pos));
                    return $"{pref} {exch}";
                }
                break;
            }
            case 16: // TXT
            case 99: { // SPF
                return DecodeTxtWireStrings(rawData);
            }
            case 33 when rawData.Length >= 7: { // SRV
                ushort prio = BinaryPrimitives.ReadUInt16BigEndian(rawData.AsSpan(0, 2));
                ushort weight = BinaryPrimitives.ReadUInt16BigEndian(rawData.AsSpan(2, 2));
                ushort port = BinaryPrimitives.ReadUInt16BigEndian(rawData.AsSpan(4, 2));
                if (fullPacket != null) {
                    int pos = rdataOffset + 6;
                    string target = DomainGroupingHelper.NormalizeDnsName(ReadWireName(fullPacket, ref pos));
                    return $"{prio} {weight} {port} {target}";
                }
                break;
            }
            case 6 when fullPacket != null: { // SOA
                int pos = rdataOffset;
                string primary = DomainGroupingHelper.NormalizeDnsName(ReadWireName(fullPacket, ref pos));
                string admin = DomainGroupingHelper.NormalizeDnsName(ReadWireName(fullPacket, ref pos));
                if (pos + 20 <= fullPacket.Length) {
                    uint serial = BinaryPrimitives.ReadUInt32BigEndian(fullPacket.AsSpan(pos, 4));
                    uint refresh = BinaryPrimitives.ReadUInt32BigEndian(fullPacket.AsSpan(pos + 4, 4));
                    uint retry = BinaryPrimitives.ReadUInt32BigEndian(fullPacket.AsSpan(pos + 8, 4));
                    uint expire = BinaryPrimitives.ReadUInt32BigEndian(fullPacket.AsSpan(pos + 12, 4));
                    uint minTtl = BinaryPrimitives.ReadUInt32BigEndian(fullPacket.AsSpan(pos + 16, 4));
                    return $"{primary} {admin} {serial} {refresh} {retry} {expire} {minTtl}";
                }
                break;
            }
            case 257: { // CAA
                return DecodeCaaRdata(rawData);
            }
            case 64: // SVCB
            case 65: { // HTTPS
                return DecodeSvcbRdata(rawData, fullPacket, rdataOffset);
            }
        }

        return $"\\# {rawData.Length} {Convert.ToHexString(rawData).ToLowerInvariant()}";
    }

    internal static string DecodeCaaRdata(byte[] raw) {
        if (raw.Length < 2) {
            return Convert.ToHexString(raw).ToLowerInvariant();
        }

        byte flags = raw[0];
        byte tagLength = raw[1];
        if (2 + tagLength > raw.Length) {
            return Convert.ToHexString(raw).ToLowerInvariant();
        }

        string tag = Encoding.ASCII.GetString(raw, 2, tagLength).ToLowerInvariant();
        string val = Encoding.UTF8.GetString(raw, 2 + tagLength, raw.Length - 2 - tagLength);
        return $"{flags} {tag} \"{val}\"";
    }

    private static string DecodeSvcbRdata(byte[] raw, byte[]? fullPacket, int rdataOffset) {
        if (raw.Length < 3) {
            return $"\\# {raw.Length} {Convert.ToHexString(raw).ToLowerInvariant()}";
        }

        ushort priority = BinaryPrimitives.ReadUInt16BigEndian(raw.AsSpan(0, 2));
        string target;
        int paramStart;

        if (fullPacket != null) {
            int pos = rdataOffset + 2;
            target = DomainGroupingHelper.NormalizeDnsName(ReadWireName(fullPacket, ref pos));
            paramStart = pos - rdataOffset;
        } else {
            int pos = 2;
            target = DomainGroupingHelper.NormalizeDnsName(ReadWireName(raw, ref pos));
            paramStart = pos;
        }

        if (string.IsNullOrEmpty(target)) {
            target = ".";
        }

        if (paramStart >= raw.Length) {
            return $"{priority} {target}";
        }

        List<string> paramsList = new();
        int idx = paramStart;
        while (idx + 4 <= raw.Length) {
            ushort key = BinaryPrimitives.ReadUInt16BigEndian(raw.AsSpan(idx, 2));
            ushort len = BinaryPrimitives.ReadUInt16BigEndian(raw.AsSpan(idx + 2, 2));
            idx += 4;
            if (idx + len > raw.Length) {
                break;
            }

            string keyName = key switch {
                0 => "mandatory",
                1 => "alpn",
                2 => "no-default-alpn",
                3 => "port",
                4 => "ipv4hint",
                5 => "ech",
                6 => "ipv6hint",
                _ => $"key{key}"
            };

            byte[] valSlice = raw.AsSpan(idx, len).ToArray();
            paramsList.Add(len == 0 ? keyName : $"{keyName}={FormatSvcParamValue(key, valSlice)}");
            idx += len;
        }

        return paramsList.Count > 0 ? $"{priority} {target} {string.Join(' ', paramsList)}" : $"{priority} {target}";
    }

    private static string FormatSvcParamValue(ushort key, byte[] val) {
        switch (key) {
            case 1: { // alpn
                List<string> alpns = new();
                int i = 0;
                while (i < val.Length) {
                    byte l = val[i++];
                    if (i + l <= val.Length) {
                        alpns.Add(Encoding.ASCII.GetString(val, i, l));
                        i += l;
                    } else {
                        break;
                    }
                }
                return string.Join(',', alpns);
            }
            case 3 when val.Length == 2: { // port
                return BinaryPrimitives.ReadUInt16BigEndian(val).ToString(CultureInfo.InvariantCulture);
            }
            case 4 when val.Length % 4 == 0: { // ipv4hint
                List<string> ips = new();
                for (int i = 0; i < val.Length; i += 4) {
                    ips.Add(new IPAddress(val.AsSpan(i, 4)).ToString());
                }
                return string.Join(',', ips);
            }
            case 6 when val.Length % 16 == 0: { // ipv6hint
                List<string> ips = new();
                for (int i = 0; i < val.Length; i += 16) {
                    ips.Add(new IPAddress(val.AsSpan(i, 16)).ToString());
                }
                return string.Join(',', ips);
            }
            default:
                return Convert.ToHexString(val).ToLowerInvariant();
        }
    }

    private static string DecodeTxtWireStrings(byte[] raw) {
        StringBuilder sb = new();
        int idx = 0;
        while (idx < raw.Length) {
            byte segLen = raw[idx++];
            int take = Math.Min(segLen, raw.Length - idx);
            if (take > 0) {
                sb.Append(Encoding.UTF8.GetString(raw, idx, take));
                idx += take;
            }
        }
        return sb.ToString();
    }

    private static string ReadWireName(byte[] packet, ref int offset) {
        StringBuilder sb = new();
        int current = offset;
        bool jumped = false;
        int jumpCount = 0;

        while (current < packet.Length) {
            byte len = packet[current];
            if (len == 0) {
                if (!jumped) {
                    offset = current + 1;
                }
                break;
            }

            if ((len & 0xC0) == 0xC0) {
                if (current + 1 >= packet.Length || ++jumpCount > 16) {
                    if (!jumped) {
                        offset = current + 2;
                    }
                    break;
                }

                int ptr = ((len & 0x3F) << 8) | packet[current + 1];
                if (!jumped) {
                    offset = current + 2;
                    jumped = true;
                }
                current = ptr;
                continue;
            }

            current++;
            int take = Math.Min((int)len, packet.Length - current);
            if (sb.Length > 0) {
                sb.Append('.');
            }
            sb.Append(Encoding.ASCII.GetString(packet, current, take));
            current += take;
            if (!jumped) {
                offset = current;
            }
        }

        return sb.ToString();
    }

    /* :: :: RFC 1035 Wire DNS Implementation :: END :: */
}

/* :: :: SystemDnsResolver :: END :: */
