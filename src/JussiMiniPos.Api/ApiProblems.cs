using Microsoft.AspNetCore.Mvc;

namespace JussiMiniPos.Api;

internal static class ApiProblems
{
    public static void Customize(ProblemDetailsContext context) =>
        Apply(context.HttpContext, context.ProblemDetails);

    public static Task WriteAsync(HttpContext context, int status, string? detail = null)
    {
        var problem = new ProblemDetails { Status = status, Detail = detail };
        Apply(context, problem);

        // Results.Problem also writes JSON when Accept does not match a registered
        // problem writer, so an HTML request cannot fall back to a developer page.
        return Results.Problem(problem).ExecuteAsync(context);
    }

    private static void Apply(HttpContext context, ProblemDetails problem)
    {
        var status = problem.Status ?? context.Response.StatusCode;
        problem.Title = status switch
        {
            StatusCodes.Status400BadRequest => "Virheellinen pyyntö",
            StatusCodes.Status401Unauthorized => "Kirjautuminen vaaditaan",
            StatusCodes.Status403Forbidden => "Ei käyttöoikeutta",
            StatusCodes.Status404NotFound => "Resurssia ei löytynyt",
            StatusCodes.Status405MethodNotAllowed => "HTTP-metodi ei ole sallittu",
            StatusCodes.Status409Conflict => "Tietojen ristiriita",
            StatusCodes.Status415UnsupportedMediaType => "Sisältötyyppiä ei tueta",
            StatusCodes.Status422UnprocessableEntity => "Pyyntöä ei voida käsitellä",
            StatusCodes.Status429TooManyRequests => "Liian monta pyyntöä",
            StatusCodes.Status503ServiceUnavailable => "Palvelu ei ole käytettävissä",
            _ => status >= 500 ? "Palvelimen virhe" : "Pyyntö epäonnistui",
        };

        if (status >= 500)
        {
            problem.Detail = "Pyyntöä ei voitu käsitellä. Yritä myöhemmin uudelleen.";
        }

        // A query string may contain credentials. Only include the route.
        problem.Instance = context.Request.Path;
        problem.Extensions["traceId"] = context.TraceIdentifier;
    }
}
