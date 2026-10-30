using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Testing;

namespace JussiMiniPos.Api.Tests;

public sealed class ApiTests
{
    [Theory]
    [InlineData("Development")]
    [InlineData("Production")]
    public async Task Health_is_anonymous_and_reports_process_liveness(string environment)
    {
        await using var factory = new ApiFactory(environment);
        using var client = factory.CreateHttpsClient();

        using var response = await client.GetAsync("/api/v1/health", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        Assert.Equal("ok", body.GetProperty("status").GetString());
        Assert.Single(body.EnumerateObject());
    }

    [Fact]
    public async Task OpenAPI_describes_the_versioned_health_endpoint()
    {
        await using var factory = new ApiFactory();
        using var client = factory.CreateHttpsClient();

        using var response = await client.GetAsync("/openapi/v1.json", TestContext.Current.CancellationToken);

        response.EnsureSuccessStatusCode();
        var document = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        Assert.Equal("JussiMiniPos API", document.GetProperty("info").GetProperty("title").GetString());
        Assert.Equal("v1", document.GetProperty("info").GetProperty("version").GetString());
        var operation = document.GetProperty("paths").GetProperty("/api/v1/health").GetProperty("get");
        Assert.Equal("GetHealth", operation.GetProperty("operationId").GetString());
        Assert.True(operation.GetProperty("responses").TryGetProperty("200", out _));
    }

    [Fact]
    public async Task Documentation_serves_its_JavaScript_locally()
    {
        await using var factory = new ApiFactory();
        // Scalar redirects /docs to /docs/ before rendering the document.
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost"),
        });

        using var response = await client.GetAsync("/docs", TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();
        Assert.Equal("text/html", response.Content.Headers.ContentType?.MediaType);
        var html = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Contains("JussiMiniPos API", html);
        Assert.Contains("openapi/v1.json", html);
        var scripts = Regex.Matches(html, "src=[\"']([^\"']+\\.js)[\"']");
        Assert.NotEmpty(scripts);
        Assert.DoesNotContain("cdn.jsdelivr.net", html);
        var documentUri = Assert.IsType<Uri>(response.RequestMessage?.RequestUri);
        foreach (Match script in scripts)
        {
            var source = new Uri(documentUri, script.Groups[1].Value);
            Assert.Equal(documentUri.Authority, source.Authority);
            Assert.Equal("https", source.Scheme);
            using var bundle = await client.GetAsync(source, TestContext.Current.CancellationToken);
            bundle.EnsureSuccessStatusCode();
            Assert.Equal("text/javascript", bundle.Content.Headers.ContentType?.MediaType);
            Assert.NotEmpty(await bundle.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        }
    }

    [Theory]
    [InlineData("/docs")]
    [InlineData("/openapi/v1.json")]
    public async Task Documentation_is_disabled_in_production(string path)
    {
        await using var factory = new ApiFactory("Production");
        using var client = factory.CreateHttpsClient();

        using var response = await client.GetAsync(path, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("Resurssia ei löytynyt", (await ReadProblem(response)).Title);
    }

    [Fact]
    public async Task Unknown_routes_return_Finnish_problems_without_echoing_the_query()
    {
        await using var factory = new ApiFactory();
        using var client = factory.CreateHttpsClient();

        using var response = await client.GetAsync("/api/v1/missing?token=do-not-echo", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var problem = await ReadProblem(response);
        Assert.Equal(404, problem.Status);
        Assert.Equal("Resurssia ei löytynyt", problem.Title);
        Assert.Equal("/api/v1/missing", problem.Instance);
        Assert.True(problem.Extensions.ContainsKey("traceId"));
        Assert.DoesNotContain("do-not-echo", await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Unsupported_methods_return_a_problem_and_preserve_the_Allow_header()
    {
        await using var factory = new ApiFactory();
        using var client = factory.CreateHttpsClient();

        using var response = await client.PostAsync("/api/v1/health", null, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.MethodNotAllowed, response.StatusCode);
        Assert.Contains("GET", response.Content.Headers.Allow);
        Assert.Equal("HTTP-metodi ei ole sallittu", (await ReadProblem(response)).Title);
    }

    [Theory]
    [InlineData("Development", "application/json")]
    [InlineData("Development", "text/html")]
    [InlineData("Production", "application/json")]
    [InlineData("Production", "text/html")]
    public async Task Unhandled_errors_never_expose_exception_details(string environment, string accept)
    {
        await using var factory = new ApiFactory(environment);
        using var client = factory.CreateHttpsClient();
        client.DefaultRequestHeaders.Accept.ParseAdd(accept);

        using var response = await client.GetAsync("/test/failure", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        var problem = await ReadProblem(response);
        Assert.Equal(500, problem.Status);
        Assert.Equal("Palvelimen virhe", problem.Title);
        Assert.Equal("Pyyntöä ei voitu käsitellä. Yritä myöhemmin uudelleen.", problem.Detail);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.DoesNotContain("Internal test exception details", body);
        Assert.DoesNotContain("InvalidOperationException", body);
    }

    [Theory]
    [InlineData(400, "Virheellinen pyyntö")]
    [InlineData(401, "Kirjautuminen vaaditaan")]
    [InlineData(403, "Ei käyttöoikeutta")]
    [InlineData(409, "Tietojen ristiriita")]
    [InlineData(415, "Sisältötyyppiä ei tueta")]
    [InlineData(422, "Pyyntöä ei voida käsitellä")]
    [InlineData(429, "Liian monta pyyntöä")]
    [InlineData(503, "Palvelu ei ole käytettävissä")]
    public async Task Empty_error_responses_are_written_as_Finnish_problems(int status, string title)
    {
        await using var factory = new ApiFactory();
        using var client = factory.CreateHttpsClient();

        using var response = await client.GetAsync($"/test/status/{status}", TestContext.Current.CancellationToken);

        Assert.Equal(status, (int)response.StatusCode);
        var problem = await ReadProblem(response);
        Assert.Equal(status, problem.Status);
        Assert.Equal(title, problem.Title);
    }

    [Fact]
    public async Task HTTP_is_rejected_even_without_an_HTTPS_redirect_port()
    {
        await using var factory = new ApiFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("http://localhost"),
            AllowAutoRedirect = false,
        });

        using var response = await client.GetAsync("/api/v1/health", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("Käytä HTTPS-yhteyttä rajapinnan kutsumiseen.", (await ReadProblem(response)).Detail);
    }

    private static async Task<ProblemDetails> ReadProblem(HttpResponseMessage response)
    {
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        return Assert.IsType<ProblemDetails>(await response.Content.ReadFromJsonAsync<ProblemDetails>(TestContext.Current.CancellationToken));
    }
}
