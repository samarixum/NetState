namespace NetState.Server.Services;

using System;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Linq;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.EntityFrameworkCore;
using NetState.Server.Data;
using NetState.Shared.Models;
using AngleSharp;
using AngleSharp.Html.Parser;

public class MonitoringBackgroundService : BackgroundService {
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<MonitoringBackgroundService> _logger;
    private readonly HttpClient _httpClient;
    private readonly HttpClient _mailClient;

    /* :: :: Constructors :: START :: */

    public MonitoringBackgroundService(
        IServiceProvider serviceProvider, 
        ILogger<MonitoringBackgroundService> logger
    ) {
        _serviceProvider = serviceProvider;
        _logger = logger;
        
        var handler = new HttpClientHandler { AllowAutoRedirect = false };
        _httpClient = new HttpClient(handler);
        _mailClient = new HttpClient();
    }

    /* :: :: Constructors :: END :: */
    // //
    /* :: :: Methods :: START :: */

    protected override async Task ExecuteAsync(CancellationToken stoppingToken) {
        _logger.LogInformation("NetState Monitoring Service started.");

        while (!stoppingToken.IsCancellationRequested) {
            _logger.LogInformation("NetState: Running monitoring loop...");
            
            using (var scope = _serviceProvider.CreateScope()) {
                var dbContext = scope.ServiceProvider.GetRequiredService<NetStateDbContext>();
                var domains = await dbContext.Domains.ToListAsync(stoppingToken);

                foreach (var domain in domains) {
                    await CheckDomainAsync(domain, stoppingToken);
                }

                await dbContext.SaveChangesAsync(stoppingToken);
            }

            await Task.Delay(TimeSpan.FromMinutes(5), stoppingToken);
        }
    }

    private async Task CheckDomainAsync(MonitoredDomain domain, CancellationToken ct) {
        var previousStatus = domain.LastStatus;
        
        try {
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

    /* :: :: Methods :: END :: */
}
