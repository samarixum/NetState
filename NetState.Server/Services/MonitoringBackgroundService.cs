using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NetState.Server.Data;
using NetState.Shared.Core;

namespace NetState.Server.Services;

/* :: :: MonitoringBackgroundService :: START :: */

/// <summary>
/// Periodically executes bounded parallel health checks for all registered monitored entries and persists results.
/// </summary>
public class MonitoringBackgroundService : BackgroundService {
    private const int MaxCheckConcurrency = 12;

    private readonly IServiceProvider _serviceProvider;
    private readonly DomainChecker _domainChecker;

    /* :: :: Constructors :: START :: */

    /// <summary>
    /// Initializes a new instance of the <see cref="MonitoringBackgroundService"/> class.
    /// </summary>
    /// <param name="serviceProvider">The dependency injection service provider.</param>
    /// <param name="domainChecker">The domain check execution service.</param>
    public MonitoringBackgroundService(
        IServiceProvider serviceProvider,
        DomainChecker domainChecker
    ) {
        _serviceProvider = serviceProvider;
        _domainChecker = domainChecker;
    }

    /* :: :: Constructors :: END :: */
    // //
    /* :: :: Background Loop :: START :: */

    /// <summary>
    /// Runs the monitoring check loop on a 5-minute schedule until shutdown is requested.
    /// </summary>
    /// <param name="stoppingToken">Token signaled when the host is stopping.</param>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken) {
        Diagnostics.Log("NetState Monitoring Service started.");

        while (!stoppingToken.IsCancellationRequested) {
            try {
                Diagnostics.Log("NetState: Running monitoring loop...");

                using (IServiceScope scope = _serviceProvider.CreateScope()) {
                    NetStateDbContext dbContext = scope.ServiceProvider.GetRequiredService<NetStateDbContext>();
                    List<NetState.Shared.Models.MonitoredDomain> domains = await dbContext.Domains.ToListAsync(stoppingToken);

                    if (domains.Count > 0) {
                        await Parallel.ForEachAsync(
                            domains,
                            new ParallelOptions {
                                MaxDegreeOfParallelism = MaxCheckConcurrency,
                                CancellationToken = stoppingToken
                            },
                            async (domain, ct) => {
                                await _domainChecker.CheckDomainAsync(domain, ct, domains);
                            }
                        );

                        await dbContext.SaveChangesAsync(stoppingToken);
                    }
                }
            } catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) {
                break;
            } catch (Exception ex) {
                Diagnostics.Bug("Error in MonitoringBackgroundService loop", ex);
            }

            await Task.Delay(TimeSpan.FromMinutes(5), stoppingToken);
        }
    }

    /* :: :: Background Loop :: END :: */
}

/* :: :: MonitoringBackgroundService :: END :: */
