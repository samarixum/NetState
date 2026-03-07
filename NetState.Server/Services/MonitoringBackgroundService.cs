namespace NetState.Server.Services;

using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.EntityFrameworkCore;
using NetState.Server.Data;
using NetState.Shared.Core;

public class MonitoringBackgroundService : BackgroundService {
    private readonly IServiceProvider _serviceProvider;
    private readonly DomainChecker _domainChecker;

    public MonitoringBackgroundService(
        IServiceProvider serviceProvider, 
        DomainChecker domainChecker
    ) {
        _serviceProvider = serviceProvider;
        _domainChecker = domainChecker;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken) {
        Diagnostics.Log("NetState Monitoring Service started.");

        while (!stoppingToken.IsCancellationRequested) {
            Diagnostics.Log("NetState: Running monitoring loop...");
            
            using (var scope = _serviceProvider.CreateScope()) {
                var dbContext = scope.ServiceProvider.GetRequiredService<NetStateDbContext>();
                var domains = await dbContext.Domains.ToListAsync(stoppingToken);

                foreach (var domain in domains) {
                    await _domainChecker.CheckDomainAsync(domain, stoppingToken);
                }

                await dbContext.SaveChangesAsync(stoppingToken);
            }

            await Task.Delay(TimeSpan.FromMinutes(5), stoppingToken);
        }
    }
}
