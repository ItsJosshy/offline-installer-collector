using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
namespace InstallerVault;
public static class Auth {
 static readonly SemaphoreSlim SetupLock=new(1,1);
 public record Credentials(string Username,string Password,string? SetupSecret);
 public static void MapAuth(this WebApplication app){
  app.MapGet("/api/auth",async(HttpContext ctx,VaultDb db,IAntiforgery csrf)=>Results.Ok(new{authenticated=ctx.User.Identity?.IsAuthenticated??false,setupRequired=!await db.Admins.AnyAsync(),csrfToken=csrf.GetAndStoreTokens(ctx).RequestToken,username=ctx.User.Identity?.Name})).AllowAnonymous();
  app.MapPost("/api/auth/setup",async(Credentials input,HttpContext ctx,VaultDb db,VaultOptions options)=>{
   await SetupLock.WaitAsync(ctx.RequestAborted);
   try {
    if(await db.Admins.AnyAsync())return Results.Conflict(new{error="Administrator is already configured."});
    if(!CryptographicOperations.FixedTimeEquals(SHA256.HashData(Encoding.UTF8.GetBytes(input.SetupSecret??"")),SHA256.HashData(Encoding.UTF8.GetBytes(options.SetupSecret??Guid.NewGuid().ToString()))))return Results.Json(new{error="Invalid setup secret."},statusCode:403);
    if(input.Password.Length<12||input.Password.Length>256||string.IsNullOrWhiteSpace(input.Username)||input.Username.Length>80)return Results.BadRequest(new{error="Use a username and a password of 12–256 characters."});
    var admin=new Admin{Username=input.Username.Trim()};admin.PasswordHash=new PasswordHasher<Admin>().HashPassword(admin,input.Password);db.Admins.Add(admin);await db.SaveChangesAsync();await SignIn(ctx,admin);return Results.Ok(new{success=true});
   } finally{SetupLock.Release();}
  }).AllowAnonymous().RequireRateLimiting("login");
  app.MapPost("/api/auth/login",async(Credentials input,HttpContext ctx,VaultDb db)=>{
   var admin=await db.Admins.SingleOrDefaultAsync();
   if(admin==null||input.Password.Length>256||new PasswordHasher<Admin>().VerifyHashedPassword(admin,admin.PasswordHash,input.Password)==PasswordVerificationResult.Failed||admin.Username!=input.Username)return Results.Json(new{error="Invalid username or password."},statusCode:401);
   await SignIn(ctx,admin);return Results.Ok(new{success=true});
  }).AllowAnonymous().RequireRateLimiting("login");
  app.MapPost("/api/auth/logout",async(HttpContext ctx)=>{await ctx.SignOutAsync();return Results.Ok(new{success=true});}).RequireAuthorization();
 }
 static Task SignIn(HttpContext c,Admin a)=>c.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme,new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier,a.Id.ToString()),new Claim(ClaimTypes.Name,a.Username)],CookieAuthenticationDefaults.AuthenticationScheme)),new AuthenticationProperties{IsPersistent=false});
}
