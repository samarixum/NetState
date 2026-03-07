using System;
using System.Collections.Generic;

namespace NetState.Shared.Models
{
    public enum ExpectationType
    {
        Redirect,
        HtmlHash,
        HttpStatus
    }

    public enum CheckStatus
    {
        Unknown,
        Healthy,
        Degraded,
        Down
    }

    public class MonitoredDomain
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public required string Name { get; set; }
        public required string Url { get; set; }
        public ExpectationType Expectation { get; set; }
        public string? ExpectedValue { get; set; } // e.g., redirect URL, HTML hash, or status code
        public CheckStatus LastStatus { get; set; } = CheckStatus.Unknown;
        public string? LastError { get; set; }
        public string? LastResponseBody { get; set; }
        public Dictionary<string, string>? LastResponseHeaders { get; set; }
        public Dictionary<string, string>? ExpectedHeaders { get; set; }
        public DateTime? LastChecked { get; set; }
    }
}
