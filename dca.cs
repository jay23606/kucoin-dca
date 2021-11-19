using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using CryptoExchange.Net.Objects;
using Kucoin.Net;
using Kucoin.Net.Objects;

namespace dca
{
    
    class Program
    {
        static void Main(string[] args)
        {
            Bot bot = new Bot("SHIB-USDT", tp: 0.6m, 0m, .08m, 0.33m, .08m, 0.66m, .08m);

            Console.WriteLine("Created Martingale Bot on " + bot.pair + " with " + bot.tp + "% Take profit of total volume:");
            for(int i = 0; i < bot.orders.Length; i += 2)
            {
                Console.WriteLine("  Order #" + ((i >> 1) + 1) + " at " + bot.orders[i] + "% drop from current Ask price in amount of $" + bot.orders[i + 1] + " USDT");
            }
            Console.WriteLine("");

            bot.Start().GetAwaiter().GetResult();
        }
    }


    public class Bot
    {
        public Bot(string pair = "SHIB-USDT", decimal tp = 1.5m, params decimal[] orders)
        {
            this.pair = pair;
            this.tp = tp;
            this.orders = orders;
        }
        public bool Enabled = false;

        KucoinClient api = new KucoinClient(new KucoinClientOptions()
        {
            // Specify options for the client
            ApiCredentials = new KucoinApiCredentials(
              "api",
             "secret",
             "pass")
        });

        public decimal[] orders;
        public decimal tp, BestAsk;
        public string pair, sellOrderId = null;
        int numBuysFilled = 0, rounds = 0;
        List<string> buyOrderIds = new List<string>();
        List<decimal> quantities = new List<decimal>(); 
        List<decimal> discountPrices = new List<decimal>();

        async Task<(bool Success, string OrderId)> Buy(decimal quantity, decimal price)
        {
            var res = await api.Spot.PlaceOrderAsync(pair, clientOrderId: null, side: KucoinOrderSide.Buy, type: KucoinNewOrderType.Limit, quantity: quantity, price: price, timeInForce: KucoinTimeInForce.GoodTillCancelled);
            return (res.Success, res.Data.OrderId);
        }

        async Task<(bool Success, string OrderId)> Sell(decimal quantity, decimal price)
        {
            var res = await api.Spot.PlaceOrderAsync(pair, clientOrderId: null, side: KucoinOrderSide.Sell, type: KucoinNewOrderType.Limit, quantity: quantity, price: price, timeInForce: KucoinTimeInForce.GoodTillCancelled);
            return (res.Success, res.Data.OrderId);
        }

        public async Task CancelBuys() { for (int i = buyOrderIds.Count - 1; i >= 0; i--) try { await api.Spot.CancelOrderAsync(buyOrderIds[i]); } catch { } }

        public async Task Stop()
        {
            Enabled = false;
            await CancelBuys();
        }

        public async Task Start()
        {
            if (!(orders.Length > 1 && orders.Length % 2 == 0)) return;
            numBuysFilled = 0; buyOrderIds.Clear(); quantities.Clear(); discountPrices.Clear(); sellOrderId = null;

            var ticker = await api.Spot.GetTickerAsync(pair);
            if (!ticker.Success) throw new Exception("Getting ticker data failed");
            BestAsk = (decimal)ticker.Data.BestAsk;

            discountPrices.Add(Decimal.Round((decimal)BestAsk * ((100m - orders[0]) / 100m), 8));
            quantities.Add(Convert.ToDecimal((int)(orders[1] / discountPrices[0]) + 1));
            var buy = await Buy(quantities[0], discountPrices[0]);
            if (buy.Success)
            {
                Console.WriteLine("Placed buy limit order in amount of " + quantities[0] + " for a price of " + discountPrices[0]);
                buyOrderIds.Add(buy.OrderId);
            }
            else throw new Exception("Creating buy order #1 failed");

            Enabled = true;
            while (Enabled)
            {
                if (sellOrderId != null)
                {
                    var order = await api.Spot.GetOrderAsync(sellOrderId);
                    if (!order.Success) throw new Exception("Getting sell order info failed");
                    if (order.Data.IsActive == false)
                    {
                        rounds++;
                        Console.WriteLine("Take profit order has executed. Round " + rounds + " complete. Canceling buy limit orders and starting over.\n");
                        //probably only a single limit order at the end of list
                        await CancelBuys();
                        await this.Start();
                    }
                }

                if (numBuysFilled < buyOrderIds.Count) //no need to keep going if there are no more buy orders provided
                {
                    var order = await api.Spot.GetOrderAsync(buyOrderIds[numBuysFilled]);
                    if (!order.Success) throw new Exception("Getting buy order info failed");
                    if (order.Data.IsActive == false)
                    {
                        numBuysFilled++;
                        Console.WriteLine("Buy limit order #" + numBuysFilled + " has filled");
                        if (sellOrderId != null)
                        {
                            var res = await api.Spot.CancelOrderAsync(sellOrderId);
                            if (!res.Success) throw new Exception("Canceling sell order failed");
                            else Console.WriteLine("Cancelled original take profit order");
                        }
                        decimal totalQuantity = 0;
                        foreach (decimal quantity in quantities) totalQuantity += quantity;
                        decimal avgPrice = 0;
                        for (int i = 0; i < quantities.Count; i++) avgPrice += quantities[i] * discountPrices[i];
                        avgPrice = avgPrice / totalQuantity;
                        decimal takeProfit = Decimal.Round((decimal)avgPrice * (1m + (tp / 100m)), 8);
                        var sell = await Sell(totalQuantity, takeProfit);
                        if (!sell.Success) throw new Exception("Creating sell order failed");
                        else Console.WriteLine("Placed take profit order in amount of " + totalQuantity + " for a price of " + takeProfit);
                        sellOrderId = sell.OrderId;

                        if (orders.Length > numBuysFilled * 2)
                        {
                            discountPrices.Add(Decimal.Round((decimal)BestAsk * ((100m - orders[numBuysFilled * 2]) / 100m), 8));
                            quantities.Add(Convert.ToDecimal((int)(orders[numBuysFilled * 2 + 1] / discountPrices[numBuysFilled]) + 1));
                            var buy2 = await Buy(quantities[numBuysFilled] , discountPrices[numBuysFilled]);

                            if (buy2.Success)
                            {
                                Console.WriteLine("Placed buy limit order in amount of " + quantities[numBuysFilled] + " for a price of " + discountPrices[numBuysFilled]);
                                buyOrderIds.Add(buy2.OrderId);
                            }
                            else throw new Exception($"Creating buy order #" + (numBuysFilled + 1) + " failed");
                        }
                    }
                }
                await Task.Delay(3 * 1000);
            }
        }
    }


