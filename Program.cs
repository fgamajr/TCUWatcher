using TCUWatcher.API.BackgroundServices;
using TCUWatcher.API.Services;
using Microsoft.Extensions.Options; // Para IOptions (usado internamente pelo Configure)
using static TCUWatcher.API.Services.MongoService; // Para MongoDbSettings (se ainda precisar dessa referência direta)
using TCUWatcher.API.Middleware; // Para o middleware de exceções
using Microsoft.Extensions.DependencyInjection; // Para AddSingleton e outros métodos de extensão de DI
using Microsoft.Extensions.Logging; // Para LogLevel
using Microsoft.AspNetCore.Builder; // Para WebApplication
using Microsoft.AspNetCore.Hosting; // Para IWebHostEnvironment
using Microsoft.Extensions.Hosting; // Para IsDevelopment

var builder = WebApplication.CreateBuilder(args);

// 1. Configuração (lê de appsettings.json, appsettings.{Env}.json, UserSecrets, EnvVars)
builder.Configuration.AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
    .AddJsonFile($"appsettings.{builder.Environment.EnvironmentName}.json", optional: true, reloadOnChange: true);

if (builder.Environment.IsDevelopment())
{
    builder.Configuration.AddUserSecrets<Program>(optional: true); // Carrega User Secrets em Desenvolvimento
}
builder.Configuration.AddEnvironmentVariables(); // Carrega Variáveis de Ambiente (para Fly.io, Docker, etc.)


// 2. Logging
builder.Logging.ClearProviders();
builder.Logging.AddConsole();
builder.Logging.AddDebug();
builder.Logging.AddFilter("TCUWatcher.API", LogLevel.Information); // Logs da sua aplicação
builder.Logging.AddFilter("Microsoft.AspNetCore", LogLevel.Warning); // Reduz o "barulho" do ASP.NET Core
builder.Logging.AddFilter("Microsoft.Hosting.Lifetime", LogLevel.Information); // Logs do ciclo de vida da aplicação


// 3. Registrar serviços para Injeção de Dependência (DI)

// Configuração do MongoDB
builder.Services.Configure<MongoDbSettings>(builder.Configuration.GetSection("MongoDb"));
builder.Services.AddSingleton<IMongoService, MongoService>();

// HttpClientFactory
builder.Services.AddHttpClient(); // Cliente HTTP genérico, se necessário
builder.Services.AddHttpClient("YouTube", client =>
{
    client.Timeout = TimeSpan.FromSeconds(20); // Aumentado um pouco o timeout
    client.DefaultRequestHeaders.Add("Accept", "application/json");
});
builder.Services.AddHttpClient("Notifier", client =>
{
    client.Timeout = TimeSpan.FromSeconds(15); // Aumentado um pouco o timeout
});

// Serviços da Aplicação
builder.Services.AddSingleton<INotifierService, NotifierService>();
builder.Services.AddSingleton<IYouTubeService, YouTubeService>();
builder.Services.AddSingleton<ISyncService, SyncService>();
builder.Services.AddSingleton<IMonitoringScheduleService, MonitoringScheduleService>();

// Serviço de Background Agendado
builder.Services.AddHostedService<SyncSchedulerHostedService>();

// Suporte a Controllers e Views (para o Dashboard)
builder.Services.AddControllersWithViews()
    .AddRazorRuntimeCompilation(); // Permite recompilação de views Razor em tempo de desenvolvimento

// Swagger/OpenAPI para documentação da API
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new Microsoft.OpenApi.Models.OpenApiInfo { Title = "TCUWatcher.API", Version = "v1" });
});

// CORS (Cross-Origin Resource Sharing)
builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(
        policy =>
        {
            policy.AllowAnyOrigin() // Para produção, restrinja a origens específicas
                  .AllowAnyMethod()
                  .AllowAnyHeader();
        });
});

// Construir a aplicação
var app = builder.Build();

// 4. Configurar o pipeline de requisições HTTP

// Middleware global de tratamento de exceções (deve ser um dos primeiros)
app.UseGlobalExceptionHandler();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI(c =>
    {
        c.SwaggerEndpoint("/swagger/v1/swagger.json", "TCUWatcher.API v1");
        c.RoutePrefix = string.Empty; // Acessa Swagger UI na raiz (ex: http://localhost:5000/)
    });
    // app.UseDeveloperExceptionPage(); // O GlobalExceptionHandler já cobre isso
}
else
{
    // Em produção, você pode querer uma página de erro mais amigável ou apenas o GlobalExceptionHandler
    // app.UseExceptionHandler("/Error"); // Se tiver uma página de erro customizada
    app.UseHsts(); // Adiciona header Strict-Transport-Security
}

app.UseHttpsRedirection(); // Redireciona requisições HTTP para HTTPS

app.UseStaticFiles(); // Permite servir arquivos estáticos da pasta wwwroot

app.UseRouting(); // Habilita o roteamento de endpoints

app.UseCors(); // Aplica a política CORS configurada

// app.UseAuthentication(); // Adicione se/quando implementar autenticação
app.UseAuthorization();  // Adicione se/quando implementar autorização

// Mapear controllers para as rotas
app.MapControllers();

// Endpoint raiz para o Dashboard (alternativa se não quiser via InfoController)
app.MapControllerRoute(
    name: "dashboard",
    pattern: "/",
    defaults: new { controller = "Dashboard", action = "Index" });


// Evento de "startup" (executa após a aplicação iniciar)
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

app.Run(); // Inicia a aplicação web