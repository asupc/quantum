using log4net;
using log4net.Config;
using log4net.Repository;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi;
using MySql.Data.MySqlClient;
using Newtonsoft.Json.Serialization;
using Quantum.Application;
using Quantum.Data;
using Quantum.Entities.Config;
using Quantum.Entities.Model;
using Quantum.Utils;
using Quantum.Web.Filters;
using Quantum.Web.Middleware;
using Quartz;
using System.Data;
using System.Net;
using System.Text;

namespace Quantum.Web;

public class Startup
{
    public IConfiguration Configuration { get; }
    public IWebHostEnvironment Environment { get; }

    public static ILoggerRepository repository { get; set; }

    public Startup(IConfiguration configuration, IWebHostEnvironment env)
    {
        Configuration = configuration;
        //Configuration.InitializeSecurityConfig();
        Environment = env;
    }

    public void ConfigureServices(IServiceCollection services)
    {
        services.AddDistributedMemoryCache();
        Setting setting = SystemConfigHelper.GetSetting();

        services.AddControllers().AddControllersAsServices();

        var address = "";
        if (setting.DBType.ToLower() == "SQLite".ToLower())
        {
            address = "Filename=db/" + setting.DBAddress;
            services.AddDbContext<IQuantumDbContext, QuantumSqliteDbContext>(options =>
            {
                options.UseSqlite(address);
                options.EnableSensitiveDataLogging(false);
            });
        }
        else
        {
            address = setting.DBAddress;
                services.AddDbContext<IQuantumDbContext, QuantumMySqlDbContext>(options =>
            {
                // 字符集归一 utf8mb4：见 QuantumMySqlDbContext.NormalizeCharsetToUtf8mb4（CharSet=utf8 插 4 字节字符报 1366）
                options.UseMySQL(QuantumMySqlDbContext.NormalizeCharsetToUtf8mb4(address));
            });
        }

        services.AddQuartz(q =>
        {
        });

        services.AddQuartzHostedService(q => q.WaitForJobsToComplete = true);

        // 启动期异步注册（go-cqhttp 拉起、定时任务注册）统一由托管服务承载
        services.AddHostedService<StartupTaskHostedService>();
        // §1-5：会话过期步骤定时巡检（每 5 分钟按 ForceEndTime 清扫），不再只靠新消息触发
        services.AddHostedService<SessionStepCleanupService>();

        services.AddSingleton<AppWebSocketManager>();
        services.AddSingleton<AppWebSocketMiddleware>();
        // §1-12：WS 心跳巡检由每连接一个 Task 合并为进程内单 PeriodicTimer 全局扫描
        services.AddHostedService<AppWebSocketHeartbeatService>();

        // 标准鉴权中间件（W6 二期遗留项落地）：仅负责验签并填充 HttpContext.User，
        // 接口放行/拦截仍由 CustomAuthorizationFilter 决定，行为保持向后兼容；
        // App 全链路与管理端共用同一签名密钥（AppAuthService.IssueAccessToken）。
        services.AddAuthentication(options =>
        {
            options.DefaultAuthenticateScheme = JwtBearerDefaults.AuthenticationScheme;
            options.DefaultChallengeScheme = JwtBearerDefaults.AuthenticationScheme;
        })
        .AddJwtBearer(options =>
        {
            options.TokenValidationParameters = new TokenValidationParameters
            {
                ValidateIssuerSigningKey = true,
                // 密钥经 Resolver 动态取自 Consts：SystemConfigHelper 轮换密钥后无需重启即生效
                IssuerSigningKeyResolver = (_, _, _, _) => new[] { new SymmetricSecurityKey(Encoding.UTF8.GetBytes(Consts.SymmetricSecurityKey)) },
                ValidateIssuer = true,
                ValidIssuer = setting.SecurityIssuer,
                ValidateAudience = true,
                ValidAudience = setting.SecurityAudience,
                ClockSkew = TimeSpan.Zero
            };
            // 管理令牌吊销闸与属性过滤器同一判定（改密后旧 Manager 令牌即失效）
            options.Events = new JwtBearerEvents
            {
                OnTokenValidated = ctx =>
                {
                    if (!JwtTokenValidator.ValidateManagerNotBefore(ctx.Principal))
                    {
                        ctx.Fail("管理令牌已因改密作废");
                    }
                    return Task.CompletedTask;
                }
            };
        });

        services.AddCors(options =>
        {
            options.AddPolicy("Any", o =>
            {
                // 跨域白名单化：默认拒绝一切跨域（前后端同源部署无需 CORS）；
                // 开发环境无配置时放行以便本机联调
                var origins = (setting.AllowedOrigins ?? "")
                    .Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                if (origins.Length > 0)
                {
                    o.WithOrigins(origins).AllowAnyMethod().AllowAnyHeader();
                }
                else if (Environment.IsDevelopment())
                {
                    o.AllowAnyOrigin().AllowAnyMethod().AllowAnyHeader();
                }
                else
                {
                    o.SetIsOriginAllowed(_ => false).AllowAnyMethod().AllowAnyHeader();
                }
            });
        });

        services.AddTransient(typeof(MessageProcess));

        services.AddScoped(typeof(TaskService));
        services.AddScoped(typeof(CommandService));
        services.AddScoped(typeof(EnvService));
        services.AddScoped(typeof(NotifyService));
        services.AddScoped(typeof(OpenTriggerTaskService));
        services.AddScoped(typeof(DockerManagementService));
        services.AddScoped(typeof(UploadService));
        services.AddScoped(typeof(EnumService));
        services.AddScoped(typeof(OpenAuthService));
        services.AddScoped(typeof(LoginService));
        services.AddScoped(typeof(SystemConfigService));
        services.AddScoped(typeof(MenuService));
        services.AddScoped(typeof(LogsService));
        services.AddScoped(typeof(BookmarkService));
        services.AddScoped(typeof(CustomDataService));
        services.AddScoped(typeof(CustomDataTitleService));
        services.AddScoped(typeof(ScriptVersionService));
        services.AddScoped(typeof(AiProviderService));
        services.AddScoped(typeof(AgentService));
        services.AddScoped(typeof(AgentProposalService));
        services.AddScoped(typeof(AgentTestRunService));
        // AI 写权限工具（删除脚本/任务/env/CustomData 管理，按 t_ai_setting 四开关动态装配、默认全关）
        services.AddScoped(typeof(AgentWriteTools));
        // LLM 客户端无状态（HttpClient 按供应商缓存为静态），单例即可
        services.AddSingleton<ILlmClient, LlmClient>();
        services.AddScoped(typeof(AppAuthService));
        services.AddScoped(typeof(AppNotificationService));
        services.AddScoped(typeof(AppMessageService));
        services.AddScoped(typeof(AppPushService));
        services.AddScoped(typeof(AppUploadService));
        services.AddScoped(typeof(AppMediaService));
        services.AddScoped(typeof(AppNotifySettingService));
        services.AddScoped(typeof(QrLoginService));


        services.AddSwaggerGen(c =>
        {
            c.SwaggerDoc("v1", new OpenApiInfo
            {
                Title = "量子助手API",
                Version = "v1"
            });
            var basePath = AppContext.BaseDirectory;
            var xmlPath = Path.Combine(basePath, "Quantum.xml");
            c.IncludeXmlComments(xmlPath);
        });

        services.AddMvc(options =>
        {
            options.EnableEndpointRouting = false;
        })

        .AddNewtonsoftJson(option =>
        {
            option.SerializerSettings.ContractResolver = new DefaultContractResolver();
            option.SerializerSettings.DateTimeZoneHandling = Newtonsoft.Json.DateTimeZoneHandling.Local;
            option.SerializerSettings.DateFormatString = "yyyy-MM-dd HH:mm:ss";
            // 不忽略 null 值，确保 children 字段始终被序列化
            option.SerializerSettings.NullValueHandling = Newtonsoft.Json.NullValueHandling.Include;
        })
        .AddJsonOptions(options =>
        {
            options.JsonSerializerOptions.PropertyNamingPolicy = null;
        });

        services.AddMvc((options) =>
        {
            options.Filters.Add<ExceptionFilter>();
        }).SetCompatibilityVersion(CompatibilityVersion.Latest);

        services.Configure<ApiBehaviorOptions>(options => options.SuppressModelStateInvalidFilter = true);

        services.Configure<FormOptions>(x =>
        {
            x.MultipartBodyLengthLimit = 209_715_200;//���200M
        });

        repository = LogManager.CreateRepository("NETCoreRepository");
        XmlConfigurator.Configure(repository, new FileInfo("log4net.config"));
    }

    public void Configure(IApplicationBuilder app, IWebHostEnvironment env)
    {
        // 必须最先注册：仅当直连方在 KnownProxies（appsettings Quantum 节）内才改写
        // RemoteIpAddress/Scheme。未配置时 X-Forwarded-For 一律不采信——公网直连部署下
        // 防伪造 XFF 绕过登录限流与外触白名单
        var forwardedOptions = new ForwardedHeadersOptions
        {
            ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto
        };
        forwardedOptions.KnownIPNetworks.Clear();
        forwardedOptions.KnownProxies.Clear();
        foreach (var entry in (SystemConfigHelper.GetSetting().KnownProxies ?? "")
            .Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = entry.Split('/');
            if (parts.Length == 2 && IPAddress.TryParse(parts[0], out var network) && int.TryParse(parts[1], out var prefixLength))
            {
                forwardedOptions.KnownIPNetworks.Add(new System.Net.IPNetwork(network, prefixLength));
            }
            else if (IPAddress.TryParse(entry, out var proxy))
            {
                forwardedOptions.KnownProxies.Add(proxy);
            }
            else
            {
                throw new InvalidOperationException($"KnownProxies 配置项「{entry}」不是合法的 IP/CIDR，请修正后重启。");
            }
        }
        app.UseForwardedHeaders(forwardedOptions);

        // 单次执行建库/迁移/种子数据
        DbInitializer.Initialize(app.ApplicationServices);
        LogServiceHelper.AddLogs();
        CacheManager.InitCacheDatas();
        app.ApplicationServices.InitMessageQueue();
        AppPushDispatcher.Init(app.ApplicationServices.GetRequiredService<IServiceScopeFactory>());
        // 任务执行引擎桥：静态执行链路（TaskExcuteService）经此为每次执行创建独立 DI scope（门面直调 scoped 服务）
        TaskPluginHost.Configure(app.ApplicationServices);
        // AI Agent 桥：任务失败自动分析（开关默认关）+ 启动清理（中断运行/影子文件/过期提案）
        AgentAutoAnalyze.Configure(app.ApplicationServices);
        AgentAutoAnalyze.StartupCleanup(app.ApplicationServices);
        app.UseCors("Any");
        app.UseWebSockets();
        // App 长连接网关（JWT 握手鉴权/心跳/ACK/sync）：Web-Chat 移除后唯一的用户长连接通道
        app.UseMiddleware<AppWebSocketMiddleware>();
        if (env.IsDevelopment())
        {
            app.UseDeveloperExceptionPage();
        }
        app.UseDefaultFiles();
        var provider = new FileExtensionContentTypeProvider();
        provider.Mappings.Add(".yml", "application/x-yaml");
        app.UseStaticFiles(new StaticFileOptions()
        {
            ContentTypeProvider = provider
        });
        app.UseRouting();

        app.UseAuthentication();

        app.UseMvc(routes =>
        {
            routes.MapRoute(
                name: "default",
                template: "{controller=Home}/{action=Index}/{id?}");
        });
        // Swagger 生产环境默认关闭（完整 API 面暴露），开发环境或显式配置 Quantum:EnableSwagger 才放行
        if (env.IsDevelopment() || SystemConfigHelper.GetSetting().EnableSwagger)
        {
            app.UseSwagger();
            app.UseSwaggerUI();
        }
        // Quartz 单调度器：JobHelper 与托管服务共享同一个 DI 工厂（历史上自建 StandaloneSchedulerFactory 形成双调度器）
        JobHelper.Init(app.ApplicationServices.GetRequiredService<ISchedulerFactory>());
    }
}