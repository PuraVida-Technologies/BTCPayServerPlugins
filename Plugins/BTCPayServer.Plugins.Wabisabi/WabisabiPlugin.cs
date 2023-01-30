﻿using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.Abstractions.Contracts;
using BTCPayServer.Abstractions.Models;
using BTCPayServer.Abstractions.Services;
using BTCPayServer.Common;
using BTCPayServer.Data.Data;
using BTCPayServer.Payments;
using BTCPayServer.Payments.PayJoin;
using BTCPayServer.PayoutProcessors;
using BTCPayServer.Services.Stores;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Formatters;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.ObjectPool;
using Microsoft.Extensions.Options;
using NBitcoin;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;
using WalletWasabi.Backend.Controllers;
using WalletWasabi.Logging;
using WalletWasabi.WabiSabi.Client;
using WalletWasabi.WabiSabi.Models.Serialization;
using LogLevel = WalletWasabi.Logging.LogLevel;

namespace BTCPayServer.Plugins.Wabisabi;

public class WabisabiPlugin : BaseBTCPayServerPlugin
{
    public override IBTCPayServerPlugin.PluginDependency[] Dependencies { get; } =
    {
        new() { Identifier = nameof(BTCPayServer), Condition = ">=1.7.4" }
    };
    public override void Execute(IServiceCollection applicationBuilder)
    {
        var utxoLocker = new LocalisedUTXOLocker();
        applicationBuilder.AddSingleton(
            provider =>
            {
                var res = ActivatorUtilities.CreateInstance<WabisabiCoordinatorClientInstanceManager>(provider);
                res.UTXOLocker = utxoLocker;
                res.AddCoordinator("zkSNACKS Coordinator", "zksnacks", provider =>
                {
                    var chain = provider.GetService<IExplorerClientProvider>().GetExplorerClient("BTC").Network
                        .NBitcoinNetwork.ChainName;
                    if (chain == ChainName.Mainnet)
                    {
                        return new Uri("https://wasabiwallet.io/");
                    }

                    if (chain == ChainName.Testnet)
                    {
                        return new Uri("https://wasabiwallet.co/");
                    }

                    return new Uri("http://localhost:37127");
                });
                return res;
            });
        applicationBuilder.AddHostedService(provider =>
            provider.GetRequiredService<WabisabiCoordinatorClientInstanceManager>());
        applicationBuilder.AddSingleton<WabisabiService>();
        applicationBuilder.AddSingleton<WalletProvider>(provider => new(
            provider,
            provider.GetRequiredService<StoreRepository>(),
            provider.GetRequiredService<IBTCPayServerClientFactory>(),
            provider.GetRequiredService<IExplorerClientProvider>(),
            provider.GetRequiredService<ILoggerFactory>(),
            utxoLocker,
            provider.GetRequiredService<EventAggregator>()
        ));
        applicationBuilder.AddWabisabiCoordinator();
        applicationBuilder.AddSingleton<IWalletProvider>(provider => provider.GetRequiredService<WalletProvider>());
        applicationBuilder.AddHostedService(provider => provider.GetRequiredService<WalletProvider>());
        ;
        applicationBuilder.AddSingleton<IUIExtension>(new UIExtension("Wabisabi/StoreIntegrationWabisabiOption",
            "store-integrations-list"));
        applicationBuilder.AddSingleton<IUIExtension>(new UIExtension("Wabisabi/WabisabiNav",
            "store-integrations-nav"));
        applicationBuilder.AddSingleton<IUIExtension>(new UIExtension("Wabisabi/WabisabiDashboard",
            "dashboard"));
        applicationBuilder.AddSingleton<IUIExtension>(new UIExtension("Wabisabi/WabisabiWalletSend",
            "onchain-wallet-send"));

        applicationBuilder.AddSingleton<IPayoutProcessorFactory, WabisabiPayoutProcessor>();
        Logger.SetMinimumLevel(LogLevel.Info);
        Logger.SetModes(LogMode.DotNetLoggers);


        base.Execute(applicationBuilder);
    }

    public class WabisabiPayoutProcessor: IPayoutProcessorFactory
    {
        private readonly LinkGenerator _linkGenerator;

        public WabisabiPayoutProcessor(LinkGenerator linkGenerator)
        {
            _linkGenerator = linkGenerator;
        }
        public string Processor { get; } = "Wabisabi";
        public string FriendlyName { get; } = "Coinjoin";
        public string ConfigureLink(string storeId, PaymentMethodId paymentMethodId, HttpRequest request)
        {
           return  _linkGenerator.GetUriByAction(
                nameof(WabisabiStoreController.UpdateWabisabiStoreSettings),
                "WabisabiStore",
                new { storeId},
                request.Scheme,
                request.Host,
                request.PathBase);
        }

        public IEnumerable<PaymentMethodId> GetSupportedPaymentMethods()
        {
            return new[] {new PaymentMethodId("BTC", PaymentTypes.BTCLike)};
        }

        public Task<IHostedService> ConstructProcessor(PayoutProcessorData settings)
        {
            return Task.FromResult<IHostedService>(new ShellSerice());
        }
        public class ShellSerice:IHostedService
        {
            public Task StartAsync(CancellationToken cancellationToken)
            {
                return Task.CompletedTask;
            }

            public Task StopAsync(CancellationToken cancellationToken)
            {
                return Task.CompletedTask;
            }
        }
    }
    

    public override void Execute(IApplicationBuilder applicationBuilder,
        IServiceProvider applicationBuilderApplicationServices)
    {
        Task.Run(async () =>
        {
            var walletProvider =
                (WalletProvider)applicationBuilderApplicationServices.GetRequiredService<IWalletProvider>();
            await walletProvider.ResetWabisabiStuckPayouts();
        });

        Logger.DotnetLogger = applicationBuilderApplicationServices.GetService<ILogger<WabisabiPlugin>>();
        base.Execute(applicationBuilder, applicationBuilderApplicationServices);
    }
}
