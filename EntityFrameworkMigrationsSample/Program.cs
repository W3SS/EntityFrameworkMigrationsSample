using System;
using System.Collections.Generic;
using System.Data.SqlClient;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Polly;

namespace EntityFrameworkMigrationsSample
{
    internal class Program
    {
        private static void Main(string[] args)
        {
            var config = new ConfigurationBuilder()
                .AddEnvironmentVariables("ASPNETCORE_")
                .AddInMemoryCollection(new[]
                {
                    new KeyValuePair<string, string>("ConnectionStringOne",
                        "Server=tcp:192.168.1.56,1433;Database=MyDatabseDbOne;User Id=sa;Password=zxc12EE;"),
                    new KeyValuePair<string, string>("ConnectionStringTwo",
                        "Server=tcp:192.168.1.56,1433;Database=MyDatabseDbTwo;User Id=sa;Password=zxc12EE;")
                })
                .Build();

            new WebHostBuilder()
                .UseKestrel()
                .UseUrls("http://*:5000")
                .UseConfiguration(config)
                .ConfigureLogging((context, builder) => builder.AddConsole().AddDebug())
                .ConfigureServices((hostContext, services) =>
                {
                    var configuration = hostContext.Configuration;

                    services.AddEntityFrameworkSqlServer()
                        .AddDbContext<EventLogContext>(options =>
                            options.UseSqlServer(configuration["ConnectionStringOne"],
                                sqlServerOptionsAction: sqlOptions =>
                                {
                                    sqlOptions.MigrationsAssembly(typeof(Program).GetTypeInfo().Assembly.GetName().Name);
                                    sqlOptions.EnableRetryOnFailure(maxRetryCount: 10, maxRetryDelay: TimeSpan.FromSeconds(30), errorNumbersToAdd: null);
                                }));


                    services.AddEntityFrameworkSqlServer()
                        .AddDbContext<CustomerContext>(options =>
                            options.UseSqlServer(configuration["ConnectionStringTwo"],
                                sqlServerOptionsAction: sqlOptions =>
                                {
                                    sqlOptions.MigrationsAssembly(typeof(Program).GetTypeInfo().Assembly.GetName().Name);
                                    sqlOptions.EnableRetryOnFailure(maxRetryCount: 10, maxRetryDelay: TimeSpan.FromSeconds(30), errorNumbersToAdd: null);
                                }));
                })
                .Configure(app => app.Run(async context => await context.Response.WriteAsync("Hello World!")))
                .Build()
                .MigrateDbContext<CustomerContext>((context, services) =>
                {
                    var logger = services.GetService<ILogger<CustomerContextSeed>>();

                    new CustomerContextSeed()
                        .SeedAsync(context, logger)
                        .GetAwaiter().GetResult();
                })
                .MigrateDbContext<EventLogContext>((_, __) => { })
                .Run();
        }
    }

    public class EventLogContext : DbContext
    {
        public EventLogContext(DbContextOptions<EventLogContext> options) : base(options)
        {
        }

        public DbSet<EventLogEntry> IntegrationEventLogs { get; set; }

        protected override void OnModelCreating(ModelBuilder builder)
        {
            builder.Entity<EventLogEntry>(ConfigureEventLogEntry);
        }

        private void ConfigureEventLogEntry(EntityTypeBuilder<EventLogEntry> builder)
        {
            builder.ToTable("EventLog");

            builder.HasKey(e => e.EventId);

            builder.Property(e => e.EventId)
                .IsRequired();

            builder.Property(e => e.Name)
                .IsRequired();

            builder.Property(e => e.Content)
                .IsRequired();
        }
    }

    public class EventLogContextDesignFactory : IDesignTimeDbContextFactory<EventLogContext>
    {
        public EventLogContext CreateDbContext(string[] args)
        {
            var optionsBuilder = new DbContextOptionsBuilder<EventLogContext>()
                .UseSqlServer("Server=tcp:192.168.1.56,1433;Database=MyDatabseDbOne;User Id=sa;Password=zxc12EE;");

            return new EventLogContext(optionsBuilder.Options);
        }
    }

    public class EventLogEntry
    {
        public Guid EventId { get; set; }
        public string Name { get; set; }
        public string Content { get; set; }
    }

    public class CustomerContext : DbContext
    {
        public CustomerContext(DbContextOptions<CustomerContext> options) : base(options)
        {
        }

        public DbSet<Order> Orders { get; set; }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.ApplyConfiguration(new OrderEntityTypeConfiguration());
        }
    }

    public class Order
    {
        public int Id { get; set; }
        public DateTime OrderDate { get; set; }
        public string Description { get; set; }
    }

    public class OrderEntityTypeConfiguration : IEntityTypeConfiguration<Order>
    {
        public void Configure(EntityTypeBuilder<Order> orderConfiguration)
        {
            orderConfiguration.ToTable("Orders");

            orderConfiguration.HasKey(o => o.Id);

            orderConfiguration.Property(o => o.OrderDate)
                .IsRequired();

            orderConfiguration.Property(o => o.Description)
                .IsRequired();
        }
    }

    internal class CustomerContextSeed
    {
        public async Task SeedAsync(CustomerContext context, ILogger<CustomerContextSeed> logger)
        {
            var policy = CreatePolicy(logger, nameof(CustomerContext));

            await policy.ExecuteAsync(async () =>
            {
                context.Database.Migrate();

                if (!context.Orders.Any())
                {
                    context.Orders.Add(new Order
                    {
                        Description = "Description",
                        OrderDate = DateTime.UtcNow
                    });

                    await context.SaveChangesAsync();
                }

                await context.SaveChangesAsync();
            });
        }

        private Policy CreatePolicy(ILogger<CustomerContextSeed> logger, string prefix, int retries = 3)
        {
            return Policy.Handle<SqlException>().WaitAndRetryAsync(
                retries,
                retry => TimeSpan.FromSeconds(5),
                (exception, timeSpan, retry, ctx) =>
                {
                    logger.LogTrace(
                        $"[{prefix}] Exception {exception.GetType().Name} with message ${exception.Message} detected on attempt {retry} of {retries}");
                }
            );
        }
    }

    public class CustomerContextDesignFactory : IDesignTimeDbContextFactory<CustomerContext>
    {
        public CustomerContext CreateDbContext(string[] args)
        {
            var optionsBuilder = new DbContextOptionsBuilder<CustomerContext>()
                .UseSqlServer("Server=tcp:192.168.1.56,1433;Database=MyDatabseDbTwo;User Id=sa;Password=zxc12EE;");

            return new CustomerContext(optionsBuilder.Options);
        }
    }

    public static class WebHostExtensions
    {
        public static IWebHost MigrateDbContext<TContext>(this IWebHost webHost,
            Action<TContext, IServiceProvider> seeder) where TContext : DbContext
        {
            using (var scope = webHost.Services.CreateScope())
            {
                var services = scope.ServiceProvider;

                var logger = services.GetRequiredService<ILogger<TContext>>();

                var context = services.GetService<TContext>();

                try
                {
                    logger.LogInformation($"Migrating database associated with context {typeof(TContext).Name}");

                    var retry = Policy.Handle<SqlException>()
                        .WaitAndRetry(new[]
                        {
                            TimeSpan.FromSeconds(5),
                            TimeSpan.FromSeconds(10),
                            TimeSpan.FromSeconds(15)
                        });

                    retry.Execute(() =>
                    {
                        context.Database
                            .Migrate();

                        seeder(context, services);
                    });


                    logger.LogInformation($"Migrated database associated with context {typeof(TContext).Name}");
                }
                catch (Exception ex)
                {
                    logger.LogError(ex,
                        $"An error occurred while migrating the database used on context {typeof(TContext).Name}");
                }
            }

            return webHost;
        }
    }
}