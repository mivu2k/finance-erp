using FinanceERP.Application.Interfaces;
using FinanceERP.Infrastructure.Persistence;
using FinanceERP.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace FinanceERP.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration config)
    {
        var connectionString = config.GetConnectionString("DefaultConnection")
            ?? throw new InvalidOperationException("Connection string 'DefaultConnection' not found.");

        services.AddDbContext<AppDbContext>(options =>
            options.UseMySql(connectionString, ServerVersion.AutoDetect(connectionString),
                mysql => mysql.EnableRetryOnFailure(3)));

        services.AddScoped<IAccountService, AccountService>();
        services.AddScoped<IVoucherService, VoucherService>();
        services.AddScoped<IPaymentRequestService, PaymentRequestService>();
        services.AddScoped<IAdvanceService, AdvanceService>();
        services.AddScoped<ILoanService, LoanService>();
        services.AddScoped<IInvestmentService, InvestmentService>();
        services.AddScoped<IPettyCashService, PettyCashService>();
        services.AddScoped<IThirdPartyService, ThirdPartyService>();
        services.AddScoped<IUtilityService, UtilityService>();
        services.AddScoped<IReportService, ReportService>();
        services.AddSingleton<IExportService, ExportService>();
        services.AddScoped<INotificationService, NotificationService>();
        services.AddScoped<IReconciliationService, ReconciliationService>();
        services.AddSingleton<IAppEmailSender, EmailService>();

        return services;
    }
}
