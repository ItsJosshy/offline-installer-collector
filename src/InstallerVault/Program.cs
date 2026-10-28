using InstallerVault;
using InstallerVault.Jobs;
using InstallerVault.Providers;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using System.Threading.RateLimiting;
using System.Net;

var builder=WebApplication.CreateBuilder(args);
builder.WebHost.ConfigureKestrel(o=>o.Limits.MaxRequestBodySize=4*1024*1024);
builder.Services.Configure<VaultOptions>(builder.Configuration.GetSection("Vault"));
builder.Services.AddSingleton(sp=>sp.GetRequiredService<IOptions<VaultOptions>>().Value);
builder.Services.AddDbContext<VaultDb>((sp,o)=>o.UseSqlite("Data Source="+Path.Combine(Path.GetFullPath(sp.GetRequiredService<VaultOptions>().DataPath),"vault.db")));
builder.Services.AddDataProtection().SetApplicationName("InstallerVault");
builder.Services.AddOptions<Microsoft.AspNetCore.DataProtection.KeyManagement.KeyManagementOptions>().Configure<VaultOptions>((o,v)=>o.XmlRepository=new Microsoft.AspNetCore.DataProtection.Repositories.FileSystemXmlRepository(new DirectoryInfo(Path.Combine(Path.GetFullPath(v.DataPath),"keys")),Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance));
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme).AddCookie(o=>{o.Cookie.Name="InstallerVault.Session";o.Cookie.HttpOnly=true;o.Cookie.SameSite=SameSiteMode.Strict;o.Cookie.SecurePolicy=builder.Environment.IsProduction()?CookieSecurePolicy.Always:CookieSecurePolicy.SameAsRequest;o.ExpireTimeSpan=TimeSpan.FromHours(12);o.SlidingExpiration=true;o.Events.OnRedirectToLogin=c=>{c.Response.StatusCode=401;return Task.CompletedTask;};o.Events.OnRedirectToAccessDenied=c=>{c.Response.StatusCode=403;return Task.CompletedTask;};});
builder.Services.AddAuthorization();
builder.Services.AddAntiforgery(o=>{o.HeaderName="X-CSRF-TOKEN";o.Cookie.SameSite=SameSiteMode.Strict;o.Cookie.SecurePolicy=builder.Environment.IsProduction()?CookieSecurePolicy.Always:CookieSecurePolicy.SameAsRequest;});
builder.Services.AddRateLimiter(o=>{o.RejectionStatusCode=429;o.AddPolicy("login",ctx=>RateLimitPartition.GetFixedWindowLimiter(ctx.Connection.RemoteIpAddress?.ToString()??"local",_=>new FixedWindowRateLimiterOptions{PermitLimit=10,Window=TimeSpan.FromMinutes(1),QueueLimit=0}));});
builder.Services.Configure<ForwardedHeadersOptions>(o=>{o.ForwardedHeaders=ForwardedHeaders.XForwardedFor|ForwardedHeaders.XForwardedProto;foreach(var proxy in builder.Configuration.GetSection("TrustedProxies").Get<string[]>()??[])o.KnownProxies.Add(IPAddress.Parse(proxy));});
builder.Services.AddSingleton<PathGuard>();builder.Services.AddScoped<Scanner>();builder.Services.AddSingleton<ISafeHttp,SafeHttp>();builder.Services.AddSignalR();builder.Services.AddVaultProviders();builder.Services.AddVaultJobs();
var app=builder.Build();
var options=app.Services.GetRequiredService<VaultOptions>();
Directory.CreateDirectory(Path.GetFullPath(options.DataPath));
if(string.IsNullOrWhiteSpace(options.SetupSecret)){
 var secretPath=Path.Combine(Path.GetFullPath(options.DataPath),"setup-secret.txt");
 if(!File.Exists(secretPath)){File.WriteAllText(secretPath,Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)));if(!OperatingSystem.IsWindows())File.SetUnixFileMode(secretPath,UnixFileMode.UserRead|UnixFileMode.UserWrite);}
 options.SetupSecret=File.ReadAllText(secretPath).Trim();
}
using(var scope=app.Services.CreateScope()){var db=scope.ServiceProvider.GetRequiredService<VaultDb>();db.Database.Migrate();if(!db.Settings.Any()){db.Settings.Add(new VaultSettings());db.SaveChanges();}}
app.UseForwardedHeaders();
app.Use(async(ctx,next)=>{
 ctx.Response.Headers["X-Content-Type-Options"]="nosniff";ctx.Response.Headers["Referrer-Policy"]="no-referrer";ctx.Response.Headers["X-Frame-Options"]="DENY";
 ctx.Response.Headers["Content-Security-Policy"]="default-src 'self'; script-src 'self'; style-src 'self' 'unsafe-inline'; img-src 'self' data:; connect-src 'self'; object-src 'none'; frame-ancestors 'none'; base-uri 'self'; form-action 'self'";
 if(ctx.Request.Path.StartsWithSegments("/api"))ctx.Response.Headers.CacheControl="no-store";
 if(app.Environment.IsProduction()&&!ctx.Request.IsHttps&&ctx.Request.Path!="/health"){ctx.Response.StatusCode=400;await ctx.Response.WriteAsJsonAsync(new{error="HTTPS is required. Configure the trusted reverse proxy."});return;}
 try{await next();}catch(Exception ex)when(ex is not OperationCanceledException){
  if(ctx.Response.HasStarted)throw;
  var code=ex switch{UnauthorizedAccessException=>403,DirectoryNotFoundException or FileNotFoundException=>404,InvalidOperationException or ArgumentException or System.Text.Json.JsonException=>400,HttpRequestException=>502,_=>500};
  ctx.Response.StatusCode=code;await ctx.Response.WriteAsJsonAsync(new{error=code==500?"The operation failed. Check storage access and server configuration.":ex is HttpRequestException?"Source could not be reached safely. Check the identifier, rate limit and network connectivity.":ex.Message});
 }
});
app.UseDefaultFiles();app.UseStaticFiles();app.UseRouting();app.UseAuthentication();app.UseAuthorization();app.UseRateLimiter();
app.Use(async(ctx,next)=>{if(ctx.Request.Path.StartsWithSegments("/api")&&ctx.Request.Method is not("GET" or "HEAD" or "OPTIONS")){try{await ctx.RequestServices.GetRequiredService<IAntiforgery>().ValidateRequestAsync(ctx);}catch(AntiforgeryValidationException){ctx.Response.StatusCode=400;await ctx.Response.WriteAsJsonAsync(new{error="Session validation failed. Refresh the page and try again."});return;}}await next();});
app.MapGet("/health",()=>Results.Ok(new{status="ok"}));app.MapAuth();app.MapVaultApi();app.MapHub<ProgressHub>("/hubs/progress").RequireAuthorization();
app.MapFallbackToFile("index.html");app.Run();
public partial class Program { }
