using System.Net;
using HomeAssistantMCPSharp.Configuration;
using HomeAssistantMCPSharp.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.WindowsServices;
using Microsoft.Extensions.Options;
using Serilog;

namespace HomeAssistantMCPSharp;

public static class Program
{
    public static int Main(string[] args)
    {
        // When running as a Windows Service the working directory is
        // C:\Windows\System32, so resolve config and logs relative to the exe.
        var contentRoot = AppContext.BaseDirectory;
        var isService = WindowsServiceHelpers.IsWindowsService();

        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .WriteTo.Console()
            .WriteTo.File(
                Path.Combine(contentRoot, "logs", "hamcp-bootstrap-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 7,
                shared: true)
            .CreateBootstrapLogger();

        try
        {
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions
            {
                Args = args,
                ContentRootPath = contentRoot,
            });

            builder.Configuration
                .SetBasePath(contentRoot)
                .AddJsonFile("appsettings.json", optional: true, reloadOnChange: true)
                .AddJsonFile($"appsettings.{builder.Environment.EnvironmentName}.json", optional: true, reloadOnChange: true)
                .AddJsonFile("appsettings.Local.json", optional: true, reloadOnChange: true)
                .AddEnvironmentVariables()
                .AddEnvironmentVariables(prefix: "HAMCP_")
                .AddCommandLine(args);

            if (isService)
            {
                var svcOptions = builder.Configuration.GetSection(ServerOptions.SectionName).Get<ServerOptions>() ?? new ServerOptions();
                builder.Host.UseWindowsService(o => o.ServiceName = svcOptions.WindowsServiceName);
            }

            builder.Host.UseSerilog((ctx, services, cfg) => cfg
                .ReadFrom.Configuration(ctx.Configuration)
                .ReadFrom.Services(services)
                .Enrich.FromLogContext());

            builder.Services.Configure<HomeAssistantOptions>(
                builder.Configuration.GetSection(HomeAssistantOptions.SectionName));
            builder.Services.Configure<ServerOptions>(
                builder.Configuration.GetSection(ServerOptions.SectionName));

            // Typed HttpClient for Home Assistant REST API
            builder.Services.AddHttpClient<HomeAssistantService>((sp, http) =>
            {
                var opts = sp.GetRequiredService<IOptions<HomeAssistantOptions>>().Value;
                var baseUrl = string.IsNullOrWhiteSpace(opts.BaseUrl) ? "http://homeassistant.local:8123/" : opts.BaseUrl;
                if (!baseUrl.EndsWith('/')) baseUrl += "/";
                http.BaseAddress = new Uri(baseUrl);
                http.Timeout = TimeSpan.FromSeconds(Math.Max(1, opts.RequestTimeoutSeconds));
                if (!string.IsNullOrWhiteSpace(opts.AccessToken))
                {
                    http.DefaultRequestHeaders.Authorization =
                        new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", opts.AccessToken);
                }
                http.DefaultRequestHeaders.UserAgent.ParseAdd(
                    string.IsNullOrWhiteSpace(opts.UserAgent) ? "HomeAssistantMCPSharp" : opts.UserAgent);
                http.DefaultRequestHeaders.Accept.Add(
                    new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("application/json"));
            })
            .ConfigurePrimaryHttpMessageHandler(sp =>
            {
                var opts = sp.GetRequiredService<IOptions<HomeAssistantOptions>>().Value;
                var handler = new HttpClientHandler();
                if (opts.IgnoreCertificateErrors)
                {
                    handler.ServerCertificateCustomValidationCallback =
                        HttpClientHandler.DangerousAcceptAnyServerCertificateValidator;
                }
                return handler;
            });

            builder.Services
                .AddMcpServer()
                .WithHttpTransport()
                .WithToolsFromAssembly();

            var server = builder.Configuration.GetSection(ServerOptions.SectionName).Get<ServerOptions>() ?? new ServerOptions();
            builder.WebHost.ConfigureKestrel(k =>
            {
                if (IPAddress.TryParse(server.Host, out var ip))
                {
                    k.Listen(ip, server.Port);
                }
                else if (string.Equals(server.Host, "localhost", StringComparison.OrdinalIgnoreCase))
                {
                    k.ListenLocalhost(server.Port);
                }
                else
                {
                    k.ListenAnyIP(server.Port);
                }
            });

            var app = builder.Build();

            app.UseSerilogRequestLogging();

            // Surface any swallowed exceptions from the host as fatal log entries.
            AppDomain.CurrentDomain.UnhandledException += (_, e) =>
                Log.Fatal(e.ExceptionObject as Exception, "Unhandled exception in AppDomain");
            TaskScheduler.UnobservedTaskException += (_, e) =>
            {
                Log.Error(e.Exception, "Unobserved task exception");
                e.SetObserved();
            };

            var ha = app.Services.GetRequiredService<HomeAssistantService>();
            Log.Information(
                "HomeAssistantMCPSharp starting on http://{Host}:{Port}{Path} (read-only={ReadOnly}, ha={HaBase}, mode={Mode}, contentRoot={ContentRoot})",
                server.Host, server.Port, server.Path, ha.IsReadOnly, ha.BaseAddress,
                isService ? "WindowsService" : "Console", contentRoot);

            app.MapGet("/healthz", () => new
            {
                status = "ok",
                server = "HomeAssistantMCPSharp",
                path = server.Path,
                readOnly = ha.IsReadOnly,
                timeUtc = DateTimeOffset.UtcNow,
            });
            app.MapMcp(server.Path);

            app.Run();
            return 0;
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "Server terminated unexpectedly");
            return 1;
        }
        finally
        {
            Log.CloseAndFlush();
        }
    }
}
