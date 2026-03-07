namespace NetState.Server.Services;

using System;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using NetState.Shared.Models;
using NetState.Shared.Core;
using AngleSharp;
using AngleSharp.Html.Parser;

public class DomainChecker {
    private readonly ILogger<DomainChecker> _logger;
    private readonly HttpClient _httpClient;
    private readonly HttpClient _mailClient;

    public DomainChecker(ILogger<DomainChecker> logger) {
        _logger = logger;
        var handler = new HttpClientHandler { AllowAutoRedirect = false };
        _httpClient = new HttpClient(handler);
        _mailClient = new HttpClient();
    }

    public async Task CheckDomainAsync(MonitoredDomain domain, CancellationToken ct) {
        var previousStatus = domain.LastStatus;

        try {
            Diagnostics.Log($"Checking domain: {domain.Name} at {domain.Url}");
            var response = await _httpClient.GetAsync(domain.Url, ct);
            domain.LastChecked = DateTime.UtcNow;

            switch (domain.Expectation) {
                case ExpectationType.Redirect: {
                    if (response.StatusCode is System.Net.HttpStatusCode.Redirect or System.Net.HttpStatusCode.MovedPermanently or System.Net.HttpStatusCode.TemporaryRedirect or System.Net.HttpStatusCode.SeeOther) {
                        var redirectUrl = response.Headers.Location?.ToString();
                        if (redirectUrl != null && (string.IsNullOrEmpty(domain.ExpectedValue) || redirectUrl.Contains(domain.ExpectedValue))) {
                            domain.LastStatus = CheckStatus.Healthy;
                            domain.LastError = null;
                        } else {
                            domain.LastStatus = CheckStatus.Degraded;
                            domain.LastError = $"Expected redirect to {domain.ExpectedValue} but got {redirectUrl}";
                        }
                    } else {
                        domain.LastStatus = CheckStatus.Down;
                        domain.LastError = $"Expected redirect, but got status code {response.StatusCode}";
                    }
                    break;
                }
                case ExpectationType.HtmlHash: {
                    var html = await response.Content.ReadAsStringAsync(ct);
                    var cleanHtml = await GetCleanHtmlAsync(html);
                    var hash = ComputeSha256Hash(cleanHtml);

                    if (hash == domain.ExpectedValue) {
                         domain.LastStatus = CheckStatus.Healthy;
                         domain.LastError = null;
                    } else {
                         domain.LastStatus = CheckStatus.Degraded;
                         domain.LastError = $"HTML hash mismatch. Expected: {domain.ExpectedValue}, Got: {hash}";
                    }
                    break;
                }
                case ExpectationType.HttpStatus: {
                    if (((int)response.StatusCode).ToString() == domain.ExpectedValue) {
                        domain.LastStatus = CheckStatus.Healthy;
                        domain.LastError = null;
                    } else {
                        domain.LastStatus = CheckStatus.Down;
                        domain.LastError = $"Expected status {domain.ExpectedValue} but got {response.StatusCode}";
                    }
                    break;
                }
            }
        } catch (Exception ex) {
            domain.LastStatus = CheckStatus.Down;
            domain.LastError = $"Exception: {ex.Message}";
            _logger.LogError(ex, "Error checking domain {DomainName}", domain.Name);
        }

        // State Transition Logic
        if (previousStatus != domain.LastStatus) {
            if (domain.LastStatus == CheckStatus.Down) {
                await SendMailChannelsAlertAsync(domain, $"Domain went offline. Error: {domain.LastError}", ct);
            } else if (domain.LastStatus == CheckStatus.Healthy && previousStatus == CheckStatus.Down) {
                await SendMailChannelsAlertAsync(domain, "Domain has recovered and is now healthy.", ct);
            }
        }
    }

    private async Task SendMailChannelsAlertAsync(MonitoredDomain domain, string message, CancellationToken ct) {
        var payload = new {
            personalizations = new[] {
                new { to = new[] { new { email = "alert@yggdrasil.au", name = "Admin" } } }
            },
            from = new { email = "monitor@yggdrasil.au", name = "NetState" },
            subject = $"NetState: {domain.Name} is {domain.LastStatus}",
            content = new[] {
                new { type = "text/plain", value = message }
            }
        };

        try {
            var response = await _mailClient.PostAsJsonAsync("https://mail.yggdrasil.au/send", payload, ct);
            if (!response.IsSuccessStatusCode) {
                _logger.LogError("Failed to send MailChannels alert for {Domain}. Status: {Status}", domain.Name, response.StatusCode);
            }
        } catch (Exception ex) {
            _logger.LogError(ex, "Exception while sending MailChannels alert for {Domain}", domain.Name);
        }
    }

    private string ComputeSha256Hash(string rawData) {
        using (System.Security.Cryptography.SHA256 sha256Hash = System.Security.Cryptography.SHA256.Create()) {
            byte[] bytes = sha256Hash.ComputeHash(System.Text.Encoding.UTF8.GetBytes(rawData));
            System.Text.StringBuilder builder = new System.Text.StringBuilder();

            for (int i = 0; i < bytes.Length; i++) {
                builder.Append(bytes[i].ToString("x2"));
            }

            return builder.ToString();
        }
    }

    private async Task<string> GetCleanHtmlAsync(string html) {
        var config = Configuration.Default;
        var context = BrowsingContext.New(config);
        var parser = context.GetService<IHtmlParser>();
        var document = await parser.ParseDocumentAsync(html);

        var elementsToRemove = document.QuerySelectorAll("input[type='hidden'], script, style");
        foreach (var element in elementsToRemove) {
            element.Remove();
        }

        return document.Body?.InnerHtml ?? string.Empty;
    }
}