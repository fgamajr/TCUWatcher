using TCUWatcher.API.BackgroundServices;
using TCUWatcher.API.Services;
using Microsoft.Extensions.Options; // For IOptions (used internally by Configure)
using static TCUWatcher.API.Services.MongoService; // For MongoDbSettings
using TCUWatcher.API.Middleware; // For the global exception handler middleware
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Hosting;


var builder = WebApplication.CreateBuilder(args);

// 1. Configuration
builder.Configuration.AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
    .AddJsonFile($"appsettings.{builder.Environment.EnvironmentName}.json", optional: true, reloadOnChange: true);

if (builder.Environment.IsDevelopment())
{
    builder.Configuration.AddUserSecrets<Program>(optional: true);
}
builder.Configuration.AddEnvironmentVariables();

// 2. Logging
builder.Logging.ClearProviders();
builder.Logging.AddSimpleConsole(options => // Use AddSimpleConsole or another specific formatter
{
    options.TimestampFormat = "HH:mm:ss dd/MM/yyyy ";
    options.IncludeScopes = true; // Optional: if you use logging scopes
    // Other options like options.SingleLine = true; can also be set here
});
builder.Logging.AddDebug(); // The Debug logger has limited formatting options

builder.Logging.AddFilter("TCUWatcher.API", LogLevel.Information);
builder.Logging.AddFilter("Microsoft.AspNetCore", LogLevel.Warning);
builder.Logging.AddFilter("Microsoft.Hosting.Lifetime", LogLevel.Information);

// 3. Registrar serviços para Injeção de Dependência (DI)

// Configuração do MongoDB
builder.Services.Configure<MongoDbSettings>(builder.Configuration.GetSection("MongoDb"));
builder.Services.AddSingleton<IMongoService, MongoService>();

// HttpClientFactory
builder.Services.AddHttpClient(); 
builder.Services.AddHttpClient("YouTube", client =>
{
    client.Timeout = TimeSpan.FromSeconds(20);
    client.DefaultRequestHeaders.Add("Accept", "application/json");
});
builder.Services.AddHttpClient("Notifier", client =>
{
    client.Timeout = TimeSpan.FromSeconds(15);
});

// Serviços da Aplicação
builder.Services.AddSingleton<INotifierService, NotifierService>();
builder.Services.AddSingleton<IYouTubeService, YouTubeService>();
builder.Services.AddSingleton<ISyncService, SyncService>();
builder.Services.AddSingleton<IMonitoringScheduleService, MonitoringScheduleService>();

// Testing Title Parsing Services
builder.Services.AddSingleton<ITitleParserService, HybridTitleParserService>();

// >>> REGISTER THE NEW SERVICES FOR SNAPSHOTTING <<<
builder.Services.AddSingleton<IPhotographerService, FfmpegYtDlpPhotographerService>();
builder.Services.AddSingleton<IStorageService, LocalStorageService>();

// Parser de Títulos
builder.Services.AddHostedService<TitleProcessingService>();
builder.Services.AddSingleton<ITitleParserService, HybridTitleParserService>();


// Serviços de Background Agendados
builder.Services.AddHostedService<SyncSchedulerHostedService>();
builder.Services.AddHostedService<SnapshottingHostedService>();
builder.Services.AddHostedService<ManualUploadProcessorService>();

// Suporte a Controllers e Views (para o Dashboard)
builder.Services.AddControllersWithViews()
    .AddRazorRuntimeCompilation();

// Swagger/OpenAPI
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new Microsoft.OpenApi.Models.OpenApiInfo { Title = "TCUWatcher.API", Version = "v1" });
});

// CORS
builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(
        policy =>
        {
            policy.AllowAnyOrigin() 
                  .AllowAnyMethod()
                  .AllowAnyHeader();
        });
});

// Construir a aplicação
var app = builder.Build();

// 4. Configurar o pipeline de requisições HTTP

// Middleware global de tratamento de exceções
app.UseGlobalExceptionHandler();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI(c =>
    {
        c.SwaggerEndpoint("/swagger/v1/swagger.json", "TCUWatcher.API v1");
        c.RoutePrefix = string.Empty; 
    });
}
else
{
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseStaticFiles();
app.UseRouting();
app.UseCors();
app.UseAuthorization(); 

// Mapear controllers
app.MapControllers();

// Endpoint raiz para o Dashboard
app.MapControllerRoute(
    name: "dashboard",
    pattern: "/",
    defaults: new { controller = "Dashboard", action = "Index" });

// Evento de "startup"
app.Lifetime.ApplicationStarted.Register(() =>
{
    var logger = app.Services.GetRequiredService<ILogger<Program>>();
    logger.LogInformation("🚀 Aplicação TCU Watcher iniciada com sucesso!");
    logger.LogInformation("Ambiente: {EnvironmentName}", app.Environment.EnvironmentName);
    logger.LogInformation("Acessar Dashboard em: / (raiz) ou /dashboard");
    if (app.Environment.IsDevelopment())
    {
        logger.LogInformation("Acessar Documentação da API (Swagger) em: /");
    }
});

app.Run();