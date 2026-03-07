using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading.Tasks;
using NetState.Shared.Models;

namespace NetState.Client.Services
{
    public class MonitoringApiClient
    {
        private readonly HttpClient _httpClient;

        public MonitoringApiClient(string baseUrl)
        {
            _httpClient = new HttpClient { BaseAddress = new System.Uri(baseUrl) };
        }

        public async Task<List<MonitoredDomain>> GetDomainsAsync()
        {
            return await _httpClient.GetFromJsonAsync<List<MonitoredDomain>>("api/domains") ?? new List<MonitoredDomain>();
        }

        public async Task CreateDomainAsync(MonitoredDomain domain)
        {
            await _httpClient.PostAsJsonAsync("api/domains", domain);
        }

        public async Task DeleteDomainAsync(System.Guid id)
        {
            await _httpClient.DeleteAsync($"api/domains/{id}");
        }

        public async Task CheckDomainAsync(System.Guid id)
        {
            await _httpClient.PostAsync($"api/domains/{id}/check", null);
        }
    }
}
