using TCUWatcher.API.BackgroundServices;
using TCUWatcher.API.Services;
// using Microsoft.Extensions.Options; // Não é mais estritamente necessário aqui se MongoService não usa IOptions
// using static TCUWatcher.API.Services.MongoService; // Removido pois MongoDbSettings não é mais injetado via IOptions no MongoService desta forma
using TCUWatcher.API.Middleware;
// using Microsoft.Extensions.Configuration; // IConfiguration é injetado nos serviços, não precisa de using direto aqui para o setup básico

var builder = WebApplication.CreateBuilder(args);

// A ordem de carregamento da configuração é importante.
// WebApplication.CreateBuilder já adiciona muitas fontes padrão.
// Adicionamos explicitamente para garantir e para o logging.
Console.WriteLine($"DEBUG Program.cs: Ambiente Inicial: {builder.Environment.EnvironmentName}"); // Pode manter este log se útil

builder.Configuration
    .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
    .AddJsonFile($"appsettings.{builder.Environment.EnvironmentName}.json", optional: true, reloadOnChange: true);

if (builder.Environment.IsDevelopment())
{
    Console.WriteLine("DEBUG Program.cs: Ambiente de Desenvolvimento detectado, User Secrets serão carregados (pelo comportamento padrão do HostBuilder ou explicitamente).");
    // WebApplication.CreateBuilder já tenta adicionar UserSecrets se o atributo UserSecretsId estiver presente no csproj
    // e o ambiente for Development. Adicionar explicitamente aqui é uma redundância segura.
    builder.Configuration.AddUserSecrets<Program>(optional: true);
}

builder.Configuration.AddEnvironmentVariables(); // Variáveis de ambiente sobrescrevem o resto

// Log para verificar a configuração final (opcional, pode remover após confirmar)
Console.WriteLine($"--- VERIFICAÇÃO FINAL DA CONFIG NO PROGRAM.CS ---");
Console.WriteLine($"YouTube:ApiKey = {builder.Configuration["YouTube:ApiKey"]?.Substring(0, Math.Min(builder.Configuration["YouTube:ApiKey"]?.Length ?? 0, 10))}..."); // Mostra apenas parte da chave
Console.WriteLine($"MongoDb:ConnectionString = {builder.Configuration["MongoDb:ConnectionString"]?.Substring(0, Math.Min(builder.Configuration["MongoDb:ConnectionString"]?.Length ?? 0, 20))}..."); // Mostra apenas parte da string
Console.WriteLine($"---------------------------------------------");


builder.Logging.ClearProviders();
builder.Logging.AddConsole();
builder.Logging.AddDebug();
builder.Logging.AddFilter("TCUWatcher.API", LogLevel.Information);
builder.Logging.AddFilter("Microsoft.Hosting.Lifetime", LogLevel.Information); // Para ver logs de início/parada
builder.Logging.AddFilter("Microsoft.AspNetCore", LogLevel.Warning);


// Registra os serviços. MongoService agora recebe IConfiguration diretamente.
// Não precisamos mais de builder.Services.Configure<MongoDbSettings> se MongoService não usar IOptions<MongoDbSettings>.
builder.Services.AddSingleton<IMongoService, MongoService>();

builder.Services.AddHttpClient();
builder.Services.AddHttpClient("YouTube", client =>
{
    client.Timeout = TimeSpan.FromSeconds(15);
    client.DefaultRequestHeaders.Add("Accept", "application/json");
});
builder.Services.AddHttpClient("Notifier", client =>
{
    client.Timeout = TimeSpan.FromSeconds(10);
});

builder.Services.AddSingleton<INotifierService, NotifierService>();
builder.Services.AddSingleton<IYouTubeService, YouTubeService>();
builder.Services.AddSingleton<ISyncService, SyncService>();
builder.Services.AddSingleton<IMonitoringScheduleService, MonitoringScheduleService>();
builder.Services.AddHostedService<SyncSchedulerHostedService>();

builder.Services.AddControllersWithViews()
    .AddRazorRuntimeCompilation();

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

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

var app = builder.Build();

app.UseGlobalExceptionHandler();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseHttpsRedirection();
app.UseStaticFiles();
app.UseRouting();
app.UseCors();
app.UseAuthorization(); // Embora não estejamos usando autenticação explícita, é bom ter.
app.MapControllers();

app.Lifetime.ApplicationStarted.Register(() =>
{
    var logger = app.Services.GetRequiredService<ILogger<Program>>();
    // var config = app.Services.GetRequiredService<IConfiguration>(); // Para pegar IConfiguration aqui
    logger.LogInformation("🚀 Aplicação TCU Watcher iniciada com sucesso no ambiente: {EnvironmentName}!", app.Environment.EnvironmentName);
    // logger.LogInformation("MongoDb:ConnectionString (vista pela app pós-build): {ConnectionString}", config["MongoDb:ConnectionString"]?.Substring(0,20) + "...");
});

app.Run();