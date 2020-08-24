using System;
using System.Collections.Generic;
using System.IdentityModel.Tokens.Jwt;
using System.IO;
using System.Threading.Tasks;
using Hangfire;
using Hangfire.Annotations;
using Hangfire.Dashboard;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.ApplicationParts;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using Zebra.FileBank.Client;
using Zebra.IC.WebUI.Appsettings.Utils.Ext;
using Zebra.IC.WebUI.Services;
using Zebra.IC.WebUI.Utils.Converters;
using Zebra.InfoPath;
using Zebra.InfoPath.i18n;
using Zebra.InfoPath.Repos;
using Zebra.InfoPath.SDK.FileBank.Services;
using Zebra.InfoPath.SDK.Linehual;

namespace Zebra.IC.WebUI
{
	using H = Appsettings.Utils.StartupHelper;
	public class Startup
	{
		public Startup(IHostingEnvironment env)
		{
			var builder = new ConfigurationBuilder()
				.SetBasePath(env.ContentRootPath)
				.AddJsonFile("Appsettings/options.json", true, true)
				.AddJsonFile("Appsettings/services.json", true, true)
				.AddJsonFile("appsettings.json", true, true)
				.AddJsonFile("Appsettings/menus.json", true, true)
				.AddJsonFile($"appsettings.{env.EnvironmentName}.json", true);
			if (env.IsDevelopment())
			{
				// For more details on using the user secret store see http://go.microsoft.com/fwlink/?LinkID=532709
				builder.AddUserSecrets<Startup>();
			}
			builder.AddEnvironmentVariables();
			Configuration = builder.Build();
			Env.InitWebDirectory(env.ContentRootPath);
			H.InitializeProfiles(Configuration.GetSection("Profiles"));
		}

		public IConfigurationRoot Configuration { get; }

		// This method gets called by the runtime. Use this method to add services to the container.
		public void ConfigureServices(IServiceCollection services)
		{
			var fileConfig = InfoPath.SDK.FileBank.Config.Of(Configuration["FileBank:Username"], Configuration["FileBank:Password"], Configuration["FileBank:Endpoint"]);
			var con = Configuration.GetConnectionString("HangfireConnection");
			services.TryAddSingleton<IStringLocalizerFactory, CustomeLocalizerFactory>();
			services.AddSingleton(provider => Configuration)
					.Configure<AppConfig>(Configuration)
					.AddLocalization()
					.AddMemoryCache()
					.Configure<CustomeLocalizationOptions>(
						options => options.ResourcePaths = new Dictionary<string, string>
						{
							{ "Zebra.InfoPath.i18n", "Resources" }
						}
					)
					.CallConfigurationExtByCfg(Configuration, "Options")
					.AddScopeds(Configuration.GetSection("Scopes"))
					.AddTransients(Configuration.GetSection("Transients"))
					.AddSession(options =>
					{
						options.IdleTimeout = TimeSpan.FromHours(6);
						options.CookieHttpOnly = true;
					})
					.AddHangfire(cfg => cfg.UseSqlServerStorage(con))
					.AddScoped<IFileService, FileService>(x => new FileService(fileConfig))
					.RegisterCainiao()
					.AddGTSLinehual();
			services.AddScoped<IStorageFeeRepo, StorageFeeRepo>();
			var manager = new ApplicationPartManager();
			manager.ApplicationParts.Add(new AssemblyPart(typeof(Startup).Assembly));

			services.AddSingleton(manager);
			services.AddMvc().AddJsonOptions(options =>
							 {
								 options.SerializerSettings.Converters.Add(new CustomDateTimeConverter());
							 });

			services.Configure<UspsEvsConfig>(Configuration.GetSection("UspsEvsConfig"));
			services.AddScoped<IUspsEvsService, UspsEvsService>();

			services.Configure<PostalCodeConfig>(Configuration.GetSection("PostalCodeConfig"));
			services.AddScoped<IStorageFeeService, StorageFeeService>();
			services.AddScoped<IEndOfDayService, EndOfDayService>();
			services.AddScoped<IAuditingService, AuditingService>();
			services.AddFileBank(x => Configuration.GetSection("FileBank").Bind(x));
		}

		// This method gets called by the runtime. Use this method to configure the HTTP request pipeline.
		public void Configure(IApplicationBuilder app, IHostingEnvironment env, ILoggerFactory loggerFactory)
		{
			H.CreateFolders(env);
			JwtSecurityTokenHandler.DefaultInboundClaimTypeMap.Clear();

			loggerFactory.AddConsole(Configuration.GetSection("Logging"));
			loggerFactory.AddDebug();

			if (env.IsDevelopment())
				app.UseDeveloperExceptionPage()
					.UseDatabaseErrorPage()
					.UseBrowserLink();
			else
				app.UseDeveloperExceptionPage()
				   .UseExceptionHandler("/Home/Error");

			app.UseStaticFiles()
				.UseStaticFiles(new StaticFileOptions
				{
					ServeUnknownFileTypes = true,
					FileProvider =
						new PhysicalFileProvider(Path.Combine(Directory.GetCurrentDirectory(), @".well-known")),
					RequestPath = new PathString("/.well-known")
				})
				.UseRequestLocalization(new RequestLocalizationOptions
				{
					DefaultRequestCulture = CultureInfoOptions.DefaultRequestCulture(),
					SupportedCultures = CultureInfoOptions.SupportedCultures(),
					SupportedUICultures = CultureInfoOptions.SupportedCultures()
				})
				.UseCookieAuthentication(new CookieAuthenticationOptions
				{
					AuthenticationScheme = "Cookies",
					AutomaticAuthenticate = true,
					ExpireTimeSpan = TimeSpan.FromMinutes(60),
					SlidingExpiration = true,
					LoginPath = "/signin-oidc"
				})
				.UseOpenIdConnectAuthentication(new OpenIdConnectOptions
				{
					AuthenticationScheme = "oidc",
					SignInScheme = "Cookies",
					Authority = Configuration["IdentityServer:Host"],
					ClientId = Configuration["IdentityServer:Client"],
					ClientSecret = Configuration["IdentityServer:Secret"],
					Scope = { "openid", "profile", "ic" },
					Events = new OpenIdConnectEvents()
					{
						OnRedirectToIdentityProvider = async ctx =>
						{
							if (ctx.Request.Headers?.ContainsKey("ajax") ?? false)
							{
								ctx.HttpContext?.Session?.Clear();
								ctx.Response.Headers.Remove("Set-Cookie");
								await ctx.Response.WriteAsync("ic_reload");
								ctx.HandleResponse();
							}
						},
						OnRemoteFailure = ctx =>
						{
							ctx.Response.Redirect("/");
							ctx.HandleResponse();
							return Task.FromResult(0);
						}
					},
					RequireHttpsMetadata = false,
					ResponseType = OpenIdConnectResponseType.CodeIdToken,
					GetClaimsFromUserInfoEndpoint = true,
					SaveTokens = true,
					TokenValidationParameters = new TokenValidationParameters
					{
						NameClaimType = "name",
						RoleClaimType = "role",
					},
				})
				.UseHangfireServer(new BackgroundJobServerOptions
				{
					WorkerCount = 50,
					Queues = new[] { "critical", "default", "uncritical" }
				})
				.UseHangfireDashboard("/cron", new DashboardOptions
				{
					Authorization = new[] { new CustomAuthorizeFilter() }
				})
				.UseSession()
				.UseMvc(routes =>
				{
					routes.MapRoute(
						"cultureRoute",
						"{culture}/{controller}/{action}/{id?}",
						new { culture = "zh-CN", controller = "Home", action = "Index" });
					routes.MapRoute(
						"default",
						"{controller=Home}/{action=Index}/{id?}");
				})
				.UseDequeues();

			RecurringJob.AddOrUpdate<IEndOfDayService>(x => x.Execute(), Cron.Hourly());
			RecurringJob.AddOrUpdate<IStorageFeeService>(x => x.JFKBill(), Cron.Daily, TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time"));
			RecurringJob.AddOrUpdate<IUspsEvsService>(x => x.Dequeue(), Cron.Daily, TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time"));

			RecurringJob.AddOrUpdate<IAuditingService>(x => x.APAuditingForFedex(), Cron.Daily(), TimeZoneInfo.Local);
			RecurringJob.AddOrUpdate<IAuditingService>(x => x.ARAuditingForFedex(), Cron.Daily(), TimeZoneInfo.Local);
		}

		public class CustomAuthorizeFilter : IDashboardAuthorizationFilter
		{
			public bool Authorize([NotNull] DashboardContext context)
			{
				var httpcontext = context.GetHttpContext();
				return httpcontext.User.Identity.IsAuthenticated;
			}
		}
	}
}
