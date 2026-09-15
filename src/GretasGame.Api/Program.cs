using Dapper;
using GretasGame.Api.Scores;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();
builder.Services.AddProblemDetails();
builder.Services.AddOpenApi();

// Aspire (or docker-compose) injects ConnectionStrings__gretasgame.
builder.AddNpgsqlDataSource("gretasgame");
DefaultTypeMap.MatchNamesWithUnderscores = true;

builder.Services.AddScoped<IScoreRepository, ScoreRepository>();

var app = builder.Build();

app.UseExceptionHandler();
app.UseStatusCodePages();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.MapGet("/", () => Results.Redirect("/openapi/v1.json")).ExcludeFromDescription();
app.MapScoreEndpoints();
app.MapDefaultEndpoints();

app.Run();

// Exposes the implicit Program class to the test project.
public partial class Program;
