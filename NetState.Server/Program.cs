using Microsoft.EntityFrameworkCore;
using NetState.Server.Data;
using NetState.Server.Services;
using NetState.Shared.Models;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
// Learn more about configuring OpenAPI at https://aka.ms/aspnet/openapi
builder.Services.AddOpenApi();

// Register DbContext with SQLite
builder.Services.AddDbContext<NetStateDbContext>(options =>
    options.UseSqlite(builder.Configuration.GetConnectionString("DefaultConnection") ?? "Data Source=netstate.db"));

// Register Monitoring Service
builder.Services.AddHostedService<MonitoringBackgroundService>();

var app = builder.Build();

// Migrate on startup for simplicity in this setup
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<NetStateDbContext>();
    db.Database.EnsureCreated();
}

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseHttpsRedirection();

// API Endpoints for Domain Management
app.MapGet("/api/domains", async (NetStateDbContext db) =>
    await db.Domains.ToListAsync())
.WithName("GetDomains");

app.MapPost("/api/domains", async (NetStateDbContext db, MonitoredDomain domain) =>
{
    db.Domains.Add(domain);
    await db.SaveChangesAsync();
    return Results.Created($"/api/domains/{domain.Id}", domain);
})
.WithName("CreateDomain");

app.MapDelete("/api/domains/{id}", async (NetStateDbContext db, Guid id) =>
{
    var domain = await db.Domains.FindAsync(id);
    if (domain == null) return Results.NotFound();
    db.Domains.Remove(domain);
    await db.SaveChangesAsync();
    return Results.NoContent();
})
.WithName("DeleteDomain");

app.Run();

