using Dapper;
using GretasGame.Api.Games;
using GretasGame.Api.Scores;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();
builder.Services.AddProblemDetails();
builder.Services.AddOpenApi();

// Aspire (or docker-compose) injects ConnectionStrings__gretasgame.
AzurePostgresConnection.RequireTls(builder.Configuration, "gretasgame");
builder.AddNpgsqlDataSource("gretasgame");
DefaultTypeMap.MatchNamesWithUnderscores = true;

builder.Services.AddScoped<IScoreRepository, ScoreRepository>();
builder.Services.AddScoped<IGameSessionRepository, GameSessionRepository>();
builder.Services.AddGameSessionRateLimiting(builder.Configuration);

var app = builder.Build();

app.UseExceptionHandler();
app.UseStatusCodePages();
app.UseRateLimiter();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.MapGet("/", () => Results.Redirect("/openapi/v1.json")).ExcludeFromDescription();
app.MapScoreEndpoints();
app.MapGameSessionEndpoints();
app.MapDefaultEndpoints();

app.Run();

// Exposes the implicit Program class to the test project.
public partial class Program;
