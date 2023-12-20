﻿#nullable enable
using System;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using BTCPayServer.HostedServices;
using BTCPayServer.Lightning;
using GraphQL;
using GraphQL.Client.Http;
using GraphQL.Client.Serializer.Newtonsoft;
using Microsoft.AspNetCore.OutputCaching;
using NBitcoin;
using Newtonsoft.Json.Linq;
using Network = NBitcoin.Network;

namespace BTCPayServer.Plugins.Blink;

public class BlinkLightningClient : ILightningClient
{
    private readonly string _apiKey;
    private readonly Uri _apiEndpoint;
    public  string? WalletId { get; set; }

    public string? WalletCurrency { get; set; }

    private readonly Network _network;
    private readonly GraphQLHttpClient _client;

    public BlinkLightningClient(string apiKey, Uri apiEndpoint, string walletId, Network network, HttpClient httpClient)
    {
        _apiKey = apiKey;
        _apiEndpoint = apiEndpoint;
        WalletId = walletId;
        _network = network;
        _client = new GraphQLHttpClient(new GraphQLHttpClientOptions() {EndPoint = _apiEndpoint, WebSocketEndPoint = new Uri( "wss://" + _apiEndpoint.Host.Replace("api.", "ws.") + _apiEndpoint.PathAndQuery), ConfigureWebsocketOptions =
            options =>
            {
                options.SetRequestHeader("X-API-KEY", apiKey);
            }}, new NewtonsoftJsonSerializer(), httpClient);
        
        
    }

    public override string ToString()
    {
        return $"type=blink;server={_apiEndpoint};api-key={_apiKey}{(WalletId is null? "":$";wallet-id={WalletId}")}";
    }

    public async Task<LightningInvoice?> GetInvoice(string invoiceId,
        CancellationToken cancellation = new CancellationToken())
    {
        
        var reques = new GraphQLRequest
        {
            Query = @"
query InvoiceByPaymentHash($paymentHash: PaymentHash!, $walletId: WalletId!) {
  me {
    defaultAccount {
      walletById(walletId: $walletId) {
        invoiceByPaymentHash(paymentHash: $paymentHash) {
          createdAt
          paymentHash
          paymentRequest
          paymentSecret
          paymentStatus
        }
      }
    }
  }
}",
            OperationName = "InvoiceByPaymentHash",
            Variables = new
            {
                walletId = WalletId,
                paymentHash = invoiceId
            }
        };
        var response = await _client.SendQueryAsync<dynamic>(reques,  cancellation);
        

        return response.Data is null ? null : ToInvoice(response.Data.me.defaultAccount.walletById.invoiceByPaymentHash);
    }

    public LightningInvoice? ToInvoice(JObject invoice)
    {
        var bolt11 = BOLT11PaymentRequest.Parse(invoice["paymentRequest"].Value<string>(), _network);
        return new LightningInvoice()
        {
            Id = invoice["paymentHash"].Value<string>(),
            Amount = invoice["satoshis"] is null? bolt11.MinimumAmount:  LightMoney.Satoshis(invoice["satoshis"].Value<long>()),
                Preimage =  invoice["paymentSecret"].Value<string>(),
            PaidAt = (invoice["paymentStatus"].Value<string>()) ==  "PAID"? DateTimeOffset.UtcNow : null,
            Status =  (invoice["paymentStatus"].Value<string>()) switch
            {
                "EXPIRED" => LightningInvoiceStatus.Expired,
                "PAID" => LightningInvoiceStatus.Paid,
                "PENDING" => LightningInvoiceStatus.Unpaid
            },
            BOLT11 =  invoice["paymentRequest"].Value<string>(),
            PaymentHash = invoice["paymentHash"].Value<string>(),
            ExpiresAt = bolt11.ExpiryDate
        };
    }

    public async Task<LightningInvoice?> GetInvoice(uint256 paymentHash,
        CancellationToken cancellation = new CancellationToken())
    {
        return await GetInvoice(paymentHash.ToString(), cancellation);
    }

    public async Task<LightningInvoice[]> ListInvoices(CancellationToken cancellation = new CancellationToken())
    {
        return await ListInvoices(new ListInvoicesParams(), cancellation);
    }

    public async Task<LightningInvoice[]> ListInvoices(ListInvoicesParams request,
        CancellationToken cancellation = new CancellationToken())
    {
        var reques = new GraphQLRequest
        {
            Query = @"
query Invoices($walletId: WalletId!) {
  me {
    defaultAccount {
      walletById(walletId: $walletId) {
        invoices {
          edges {
            node {
              createdAt
              paymentHash
              paymentRequest
              paymentSecret
              paymentStatus
              ... on LnInvoice {
                satoshis
              }
            }
          }
        }
      }
    }
  }
}",
            OperationName = "Invoices",
            Variables = new
            {
                walletId = WalletId
            }
        };
        var response = await _client.SendQueryAsync<dynamic>(reques,  cancellation);

        var result = ((JArray)response.Data.me.defaultAccount.walletById.invoices.edges).Select(o => ToInvoice((JObject) o["node"] )).Where(o => o is not null || request.PendingOnly is not true || o.Status == LightningInvoiceStatus.Unpaid).ToArray();
        return (LightningInvoice[]) result;
    }

    public async Task<LightningPayment?> GetPayment(string paymentHash,
        CancellationToken cancellation = new CancellationToken())
    {
        var reques = new GraphQLRequest
        {
            Query = @"
query TransactionsByPaymentHash($paymentHash: PaymentHash!, $walletId: WalletId!) {
  me {
    defaultAccount {
      walletById(walletId: $walletId) {
        transactionsByPaymentHash(paymentHash: $paymentHash) {
          createdAt
          direction
          id
          initiationVia {
            ... on InitiationViaLn {
              paymentHash
              paymentRequest
            }
          }
          memo
          settlementAmount
          settlementCurrency
          settlementVia {
            ... on SettlementViaLn {
              preImage
            }
          }
          status
        }
      }
    }
  }
}",
            OperationName = "TransactionsByPaymentHash",
            Variables = new
            {
                walletId = WalletId,
                paymentHash = paymentHash
            }
        };
        var response = await _client.SendQueryAsync<dynamic>(reques,  cancellation);
        var item = (JArray) response.Data.me.defaultAccount.walletById.transactionsByPaymentHash;
        return item.Any()? ToLightningPayment((JObject)item.First()): null;
    }

    public LightningPayment? ToLightningPayment(JObject transaction)
    {
        if ((string)transaction["direction"] == "RECEIVE")
            return null;

        var initiationVia = transaction["initiationVia"];
        if (initiationVia["paymentHash"] == null)
            return null;

        var bolt11 = BOLT11PaymentRequest.Parse((string)initiationVia["paymentRequest"], _network);

        return new LightningPayment()
        {
            Amount = bolt11.MinimumAmount,
            Status = transaction["status"].ToString() switch
            {
                "FAILURE" => LightningPaymentStatus.Failed,
                "PENDING" => LightningPaymentStatus.Pending,
                "SUCCESS" => LightningPaymentStatus.Complete,
                _ => LightningPaymentStatus.Unknown
            },
            BOLT11 = (string)initiationVia["paymentRequest"],
            Id = (string)initiationVia["paymentHash"],
            PaymentHash = (string)initiationVia["paymentHash"],
            CreatedAt = DateTimeOffset.FromUnixTimeSeconds(transaction["createdAt"].Value<long>()),
            AmountSent = bolt11.MinimumAmount,
        };
    }

    public async Task<LightningPayment[]> ListPayments(CancellationToken cancellation = new CancellationToken())
    {
        return await ListPayments(new ListPaymentsParams(), cancellation);
    }

    public async Task<LightningPayment[]> ListPayments(ListPaymentsParams request,
        CancellationToken cancellation = new CancellationToken())
    {
        
        var reques = new GraphQLRequest
        {
            Query = @"
query Transactions($walletId: WalletId!) {
  me {
    defaultAccount {
      walletById(walletId: $walletId) {
        transactions {
          edges {
            node {
          createdAt
          direction
          id
          initiationVia {
            ... on InitiationViaLn {
              paymentHash
              paymentRequest
            }
          }
          memo
          settlementAmount
          settlementCurrency
          settlementVia {
            ... on SettlementViaLn {
              preImage
            }
          }
          status
            }
          }
        }
      }
    }
  }
}",
            OperationName = "Transactions",
            Variables = new
            {
                walletId = WalletId
            }
        };
        var response = await _client.SendQueryAsync<dynamic>(reques,  cancellation);
        
            
            
        var result = ((JArray)response.Data.me.defaultAccount.walletById.transactions.edges).Select(o => ToLightningPayment((JObject) o["node"])).Where(o => o is not null && (request.IncludePending is not true || o.Status!= LightningPaymentStatus.Pending)).ToArray();
        return (LightningPayment[]) result;
    }

    public async Task<LightningInvoice> CreateInvoice(LightMoney amount, string description, TimeSpan expiry,
        CancellationToken cancellation = new())
    {
        return await CreateInvoice(new CreateInvoiceParams(amount, description, expiry), cancellation);
    }

    public async Task<LightningInvoice> CreateInvoice(CreateInvoiceParams createInvoiceRequest,
        CancellationToken cancellation = new())
    {
        string query;
        
        query = WalletCurrency?.Equals("btc", StringComparison.InvariantCultureIgnoreCase) is not true ? @"
mutation lnInvoiceCreate($input: LnUsdInvoiceBtcDenominatedCreateOnBehalfOfRecipientInput!) {
  lnUsdInvoiceBtcDenominatedCreateOnBehalfOfRecipient(input: $input) {
    invoice {
      createdAt
      paymentHash
      paymentRequest
      paymentSecret
      paymentStatus
      satoshis
    },
    errors{
      message
    }
  }
}" : @"
mutation lnInvoiceCreate($input: LnInvoiceCreateOnBehalfOfRecipientInput!) {
  lnInvoiceCreateOnBehalfOfRecipient(input: $input) {
    invoice {
      createdAt
      paymentHash
      paymentRequest
      paymentSecret
      paymentStatus
      satoshis
    },
    errors{
      message
    }
  }
}";
        
        var reques = new GraphQLRequest
        {
            Query = query,
            OperationName = "lnInvoiceCreate",
            Variables = new
            {
                input = new
                {
                    recipientWalletId = WalletId,
                    memo = createInvoiceRequest.Description,
                    descriptionHash = createInvoiceRequest.DescriptionHash?.ToString(),
                    amount = (long)createInvoiceRequest.Amount.ToUnit(LightMoneyUnit.Satoshi),
expiresIn = (int)createInvoiceRequest.Expiry.TotalMinutes
                    
                }
            }
        };
        var response = await _client.SendQueryAsync<dynamic>(reques,  cancellation);
        var inv = (WalletCurrency?.Equals("btc", StringComparison.InvariantCultureIgnoreCase) is not true
            ? response.Data.lnUsdInvoiceBtcDenominatedCreateOnBehalfOfRecipient.invoice
            : response.Data.lnInvoiceCreateOnBehalfOfRecipient.invoice)as JObject;

        if (inv is null)
        {
            var errors = (WalletCurrency?.Equals("btc", StringComparison.InvariantCultureIgnoreCase) is not true
                ? response.Data.lnUsdInvoiceBtcDenominatedCreateOnBehalfOfRecipient.errors
                : response.Data.lnInvoiceCreateOnBehalfOfRecipient.errors) as JArray;

            if (errors.Any())
            {
                throw new Exception(errors.First()["message"].ToString());
            }
        }
        return ToInvoice(inv);
    }

    public async Task<ILightningInvoiceListener> Listen(CancellationToken cancellation = new CancellationToken())
    {
        return new BlinkListener(_client, this);
    }

    public class BlinkListener : ILightningInvoiceListener
    {
        private readonly BlinkLightningClient _lightningClient;
        private readonly Channel<LightningInvoice> _invoices = Channel.CreateUnbounded<LightningInvoice>();
        private readonly IDisposable _subscription;

        public BlinkListener(GraphQLHttpClient httpClient, BlinkLightningClient lightningClient)
        {
            try
            {

                _lightningClient = lightningClient;
                var stream = httpClient.CreateSubscriptionStream<dynamic>(new GraphQLRequest()
                {
                    Query = @"subscription myUpdates {
  myUpdates {
    update {
      ... on LnUpdate {
        transaction {
          initiationVia {
            ... on InitiationViaLn {
              paymentHash
            }
          }
          direction
        }
      }
    }
  }
}
", OperationName = "myUpdates"
                });

                _subscription = stream.Subscribe(async response =>
                {
                    try
                    {
                        var x = stream;
                        var y  = _lightningClient;
                        if (response.Data.myUpdates.update.transaction.direction != "RECEIVE")
                            return;
                        if ((await _lightningClient.GetInvoice(response.Data.myUpdates.update.transaction.initiationVia
                                .paymentHash.ToString())) is LightningInvoice inv)
                        {

                            _invoices.Writer.TryWrite(inv);
                        }
                    }
                    catch (Exception e)
                    {
                        Console.WriteLine(e);
                    }
                   
                });
            }
            catch (Exception e)
            {
                Console.WriteLine(e);
            }
        }
        public void Dispose()
        {
            _subscription.Dispose();
            _invoices.Writer.TryComplete();
        }

        public async Task<LightningInvoice> WaitInvoice(CancellationToken cancellation)
        {
            return await  _invoices.Reader.ReadAsync(cancellation);
        }
    }
    public async Task<(Network Network, string DefaultWalletId, string DefaultWalletCurrency)> GetNetworkAndDefaultWallet(CancellationToken cancellation =default)
    {
               
        var reques = new GraphQLRequest
        {
            Query = @"
query GetNetworkAndDefaultWallet {
  globals {
    network
  }
  me {
    defaultAccount {
      defaultWallet{
        id
        currency
      }
    }
  }
}",
            OperationName = "GetNetworkAndDefaultWallet"
        };
        
        var response = await _client.SendQueryAsync<dynamic>(reques,  cancellation);

        var defaultWalletId = (string) response.Data.me.defaultAccount.defaultWallet.id;
        var defaultWalletCurrency = (string) response.Data.me.defaultAccount.defaultWallet.currency;
        var network = response.Data.globals.network.ToString() switch
        {
            "mainnet" => Network.Main,
            "testnet" => Network.TestNet,
            "regtest" => Network.RegTest,
            _ => throw new ArgumentOutOfRangeException()
        };
        return (network, defaultWalletId, defaultWalletCurrency);
    }

    public Task<LightningNodeInformation> GetInfo(CancellationToken cancellation = new CancellationToken())
    {

        throw new NotSupportedException();
    }

    public async Task<LightningNodeBalance> GetBalance(CancellationToken cancellation = new())
    {
        var request = new GraphQLRequest
        {
            Query = @"
query GetWallet($walletId: WalletId!) {
  me {
    defaultAccount {
      walletById(walletId: $walletId) {
        id
        balance
        walletCurrency
      }
    }
  }
}",
            OperationName = "GetWallet",
            Variables = new {
                walletId = WalletId
            }
        };
        
        var response = await  _client.SendQueryAsync<dynamic>(request,  cancellation);

        WalletCurrency = response.Data.me.defaultAccount.walletById.walletCurrency;
        if (response.Data.me.defaultAccount.walletById.walletCurrency == "BTC")
        {
            return new LightningNodeBalance()
            {
                OffchainBalance = new OffchainBalance()
                {
                    Local = LightMoney.Satoshis((long)response.Data.me.defaultAccount.walletById.balance)
                }
            };
        }

        return new LightningNodeBalance();
    }

    public async Task<PayResponse> Pay(PayInvoiceParams payParams,
        CancellationToken cancellation = new CancellationToken())
    {
        return await Pay(null, new PayInvoiceParams(), cancellation);
    }

    public async Task<PayResponse> Pay(string bolt11, PayInvoiceParams payParams,
        CancellationToken cancellation = new CancellationToken())
    {
        
        var request = new GraphQLRequest
        {
            Query = @"
mutation LnInvoicePaymentSend($input: LnInvoicePaymentInput!) {
  lnInvoicePaymentSend(input: $input) {
    transaction {
      createdAt
          direction
          id
          initiationVia {
            ... on InitiationViaLn {
              paymentHash
              paymentRequest
            }
          }
          memo
          settlementAmount
          settlementCurrency
          settlementVia {
            ... on SettlementViaLn {
              preImage
            }
          }
          status
    }
    errors {
      message
    }
    status
  }
}",
            OperationName = "LnInvoicePaymentSend",
            Variables = new {
                input = new {
                    walletId = WalletId,
                    paymentRequest = bolt11,
                }
            }
        };
        CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(cancellation,
            new CancellationTokenSource(payParams?.SendTimeout ?? PayInvoiceParams.DefaultSendTimeout).Token);
        var response =(JObject) (await  _client.SendQueryAsync<dynamic>(request,  cts.Token)).Data.lnInvoicePaymentSend;
        
        var result = new PayResponse();
        result.Result = response["status"].Value<string>() switch
        {
            "ALREADY_PAID" => PayResult.Ok,
            "FAILURE" => PayResult.Error,
            "PENDING"=> PayResult.Unknown,
            "SUCCESS" => PayResult.Ok,
            null => PayResult.Unknown,
            _ => throw new ArgumentOutOfRangeException()
        };
        if (response["transaction"]?.Value<JObject>() is not null)
        {
            result.Details = new PayDetails()
            {
                PaymentHash = new uint256(response["transaction"]["initiationVia"]["paymentHash"].Value<string>()),
                Status = response["status"].Value<string>() switch
                {
                    "ALREADY_PAID"  => LightningPaymentStatus.Complete,
                    "FAILURE" => LightningPaymentStatus.Failed,
                    "PENDING" => LightningPaymentStatus.Pending,
                    "SUCCESS" => LightningPaymentStatus.Complete,
                    null => LightningPaymentStatus.Unknown,
                    _ => throw new ArgumentOutOfRangeException()
                },
                Preimage = response["transaction"]["settlementVia"]?["preImage"].Value<string>() is null? null: new uint256(response["transaction"]["settlementVia"]["preImage"].Value<string>()),
            };
        }

        return result;
    }

    public async Task<PayResponse> Pay(string bolt11, CancellationToken cancellation = new CancellationToken())
    {
        return await Pay(bolt11, new PayInvoiceParams(), cancellation);
    }


    public async Task CancelInvoice(string invoiceId, CancellationToken cancellation = new CancellationToken())
    {
        throw new NotImplementedException();
    }

    public async Task<BitcoinAddress> GetDepositAddress(CancellationToken cancellation = new CancellationToken())
    {
        throw new NotImplementedException();
    }

    public async Task<OpenChannelResponse> OpenChannel(OpenChannelRequest openChannelRequest,
        CancellationToken cancellation = new CancellationToken())
    {
        throw new NotImplementedException();
    }

    public async Task<ConnectionResult> ConnectTo(NodeInfo nodeInfo,
        CancellationToken cancellation = new CancellationToken())
    {
        throw new NotImplementedException();
    }

    public async Task<LightningChannel[]> ListChannels(CancellationToken cancellation = new CancellationToken())
    {
        throw new NotImplementedException();
    }
}