using JussiMiniPos.Api;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace JussiMiniPos.Api.Tests;

public sealed class ApiFactory(string environment = "Development") : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment(environment);
        // Test failures must not write to the host machine's Windows Event Log.
        builder.ConfigureLogging(logging => logging.ClearProviders());
        builder.ConfigureServices(services => services.AddSingleton<IStartupFilter, TestEndpoints>());
    }

    public HttpClient CreateHttpsClient() => CreateClient(new WebApplicationFactoryClientOptions
    {
        BaseAddress = new Uri("https://localhost"),
        AllowAutoRedirect = false,
    });

    // Failure routes exist only in the test host, never in the shipped API.
    private sealed class TestEndpoints : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            next(app);
            app.UseEndpoints(endpoints =>
            {
                endpoints.MapGet("/test/failure", Throw);
                endpoints.MapGet("/test/status/{status:int}", (int status) => Results.StatusCode(status));
            });
        };

        private static IResult Throw() => throw new InvalidOperationException("Internal test exception details");
    }
}
