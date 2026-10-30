using Scalar.AspNetCore;

namespace JussiMiniPos.Api;

public class Program
{
    public static void Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);
        builder.Services.AddProblemDetails(options =>
            options.CustomizeProblemDetails = ApiProblems.Customize);
        builder.Services.AddOpenApi("v1", options => options.AddDocumentTransformer((document, _, _) =>
        {
            document.Info.Title = "JussiMiniPos API";
            document.Info.Version = "v1";
            document.Info.Description = "JussiMiniPos-rajapinta. Tässä vaiheessa käytettävissä on palvelun tilatarkistus.";
            return Task.CompletedTask;
        }));

        var app = builder.Build();

        // Keep exception details out of responses in Development as well.
        app.UseExceptionHandler(handler => handler.Run(context =>
            ApiProblems.WriteAsync(context, StatusCodes.Status500InternalServerError)));
        app.UseStatusCodePages(context =>
            ApiProblems.WriteAsync(context.HttpContext, context.HttpContext.Response.StatusCode));
        app.Use(async (context, next) =>
        {
            // Refuse HTTP even when no HTTPS redirect port is configured.
            if (!context.Request.IsHttps)
            {
                await ApiProblems.WriteAsync(context, StatusCodes.Status400BadRequest,
                    "Käytä HTTPS-yhteyttä rajapinnan kutsumiseen.");
                return;
            }

            await next(context);
        });

        app.MapGet("/api/v1/health", () => TypedResults.Ok(new HealthResponse("ok")))
            .AllowAnonymous()
            .WithName("GetHealth")
            .WithSummary("Palvelun tilatarkistus")
            .WithDescription("Tarkistaa, että rajapinta vastaa. Tietokantayhteyttä ei tarkisteta.");

        if (app.Environment.IsDevelopment())
        {
            app.MapOpenApi();
            app.MapScalarApiReference("/docs", options => options
                .WithTitle("JussiMiniPos API")
                .DisableDefaultFonts()
                .DisableAgent());
        }

        app.Run();
    }
}

/// <summary>Process liveness only; database readiness belongs to the persistence integration.</summary>
public sealed record HealthResponse(string Status);
