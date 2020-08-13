using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NLog;
using NLog.Extensions.Logging;
using NLog.Web;

namespace TestWeb
{
    public class Program
    {
        public static void Main(string[] args)
        {
            var config = new ConfigurationBuilder()
                .SetBasePath(System.IO.Directory.GetCurrentDirectory())
                .AddJsonFile("appsettings.json", optional: true, reloadOnChange: true)
                .Build();
            LogManager.Configuration = new NLogLoggingConfiguration(config.GetSection("NLog")); // 何でこれがないとログ出ないの？
            var logger = NLogBuilder.ConfigureNLog(LogManager.Configuration).GetCurrentClassLogger();
            logger.Info("アプリケーション起動");

            var host = CreateWebHostBuilder(args).Build();
            host.Run();
        }

        public static IWebHostBuilder CreateWebHostBuilder(string[] args) =>
            WebHost
                .CreateDefaultBuilder(args)
                .UseStartup<Startup>()
                .UseNLog();
    }
}
