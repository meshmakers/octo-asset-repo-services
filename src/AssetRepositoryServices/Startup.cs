using Meshmakers.Octo.Backend.AssetRepositoryServices.Routing;
using Meshmakers.Octo.Services.Common.Cors;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Cors.Infrastructure;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

#pragma warning disable 1591

namespace Meshmakers.Octo.Backend.AssetRepositoryServices;

public class Startup
{
    public Startup(IConfiguration configuration)
    {
        Configuration = configuration;
    }

    private IConfiguration Configuration { get; }

    // This method gets called by the runtime. Use this method to add services to the container.
    public void ConfigureServices(IServiceCollection services)
    {
        services.AddSingleton<ICorsPolicyProvider, CorsPolicyProvider>();
        services.AddCors();

        services.Configure<RouteOptions>(options =>
            options.ConstraintMap.Add("tenantId", typeof(TenantIdRouteConstraint)));

        services.AddOcto(
            systemOptions => Configuration.GetSection("System").Bind(systemOptions),
            options => Configuration.GetSection("AssetRepository").Bind(options));
    }

    // This method gets called by the runtime. Use this method to configure the HTTP request pipeline.
    public void Configure(IApplicationBuilder app, IWebHostEnvironment env)
    {
        if (env.IsDevelopment())
        {
            app.UseDeveloperExceptionPage();
        }
        else
            // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
        {
            app.UseHsts();
        }

        app.UseCors();

        app.UseOcto();

        app.UseHttpsRedirection();

        app.UseStaticFiles();
    }
}
