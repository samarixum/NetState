using System;
using System.Net.Http;
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

namespace NetState.Server.Services
{
    public class MonitoringBackgroundService : BackgroundService
    {
        private readonly IServiceProvider _serviceProvider;
        private readonly ILogger<MonitoringBackgroundService> _logger;
        private readonly HttpClient _httpClient;

        public MonitoringBackgroundService(
            IServiceProvider serviceProvider, 
            ILogger<MonitoringBackgroundService> logger)
        {
            _serviceProvider = serviceProvider;
            _logger = logger;
            
            var handler = new HttpClientHandler { AllowAutoRedirect = false };
            _httpClient = new HttpClient(handler);
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("NetState Monitoring Service started.");

            while (!stoppingToken.IsCancellationRequested)
            {
                _logger.LogInformation("NetState: Running monitoring loop...");
                
                using (var scope = _serviceProvider.CreateScope())
                {
                    var dbContext = scope.ServiceProvider.GetRequiredService<NetStateDbContext>();
                    var domains = await dbContext.Domains.ToListAsync(stoppingToken);

                    foreach (var domain in domains)
                    {
                        await CheckDomainAsync(domain, stoppingToken);
                    }

                    await dbContext.SaveChangesAsync(stoppingToken);
                }

                await Task.Delay(TimeSpan.FromMinutes(5), stoppingToken);
            }
        }

        private async Task CheckDomainAsync(MonitoredDomain domain, CancellationToken ct)
        {
            try
            {
                var response = await _httpClient.GetAsync(domain.Url, ct);
                domain.LastChecked = DateTime.UtcNow;

                switch (domain.Expectation)
                {
                    case ExpectationType.Redirect:
                        if (response.StatusCode is System.Net.HttpStatusCode.Redirect or System.Net.HttpStatusCode.MovedPermanently or System.Net.HttpStatusCode.TemporaryRedirect or System.Net.HttpStatusCode.SeeOther)
                        {
                            var redirectUrl = response.Headers.Location?.ToString();
                            if (redirectUrl != null && (string.IsNullOrEmpty(domain.ExpectedValue) || redirectUrl.Contains(domain.ExpectedValue)))
                            {
                                domain.LastStatus = CheckStatus.Healthy;
                                domain.LastError = null;
                            }
                            else
                            {
                                domain.LastStatus = CheckStatus.Degraded;
                                domain.LastError = $"Expected redirect to {domain.ExpectedValue} but got {redirectUrl}";
                            }
                        }
                        else
                        {
                            domain.LastStatus = CheckStatus.Down;
                            domain.LastError = $"Expected redirect, but got status code {response.StatusCode}";
                        }
                        break;
                    
                    case ExpectationType.HtmlHash:
                        var html = await response.Content.ReadAsStringAsync(ct);
                        var cleanHtml = await GetCleanHtmlAsync(html);
                        // Simplified hash check for now
                        if (cleanHtml.Length > 0) // Placeholder
                        {
                             domain.LastStatus = CheckStatus.Healthy;
                             domain.LastError = null;
                        }
                        break;

                    case ExpectationType.HttpStatus:
                        if (((int)response.StatusCode).ToString() == domain.ExpectedValue)
                        {
                            domain.LastStatus = CheckStatus.Healthy;
                            domain.LastError = null;
                        }
                        else
                        {
                            domain.LastStatus = CheckStatus.Down;
                            domain.LastError = $"Expected status {domain.ExpectedValue} but got {response.StatusCode}";
                        }
                        break;
                }
            }
            catch (Exception ex)
            {
                domain.LastStatus = CheckStatus.Down;
                domain.LastError = $"Exception: {ex.Message}";
                _logger.LogError(ex, "Error checking domain {DomainName}", domain.Name);
            }
        }

        private async Task<string> GetCleanHtmlAsync(string html)
        {
            var config = Configuration.Default;
            var context = BrowsingContext.New(config);
            var parser = context.GetService<IHtmlParser>();
            var document = await parser.ParseDocumentAsync(html);
            
            // Basic dynamic element removal
            var elementsToRemove = document.QuerySelectorAll("input[type='hidden'], script, style");
            foreach (var element in elementsToRemove)
            {
                element.Remove();
            }

            return document.Body?.InnerHtml ?? string.Empty;
        }
    }
}
