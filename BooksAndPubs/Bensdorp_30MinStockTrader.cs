﻿//==============================================================================
// Project:     TuringTrader, algorithms from books & publications
// Name:        Bensdorp_30MinStockTrader
// Description: Strategy, as published in Laurens Bensdorp's book
//              'The 30-Minute Stock Trader'.
// History:     2019iii19, FUB, created
//------------------------------------------------------------------------------
// Copyright:   (c) 2011-2019, Bertram Solutions LLC
//              http://www.bertram.solutions
// License:     This code is licensed under the term of the
//              GNU Affero General Public License as published by 
//              the Free Software Foundation, either version 3 of 
//              the License, or (at your option) any later version.
//              see: https://www.gnu.org/licenses/agpl-3.0.en.html
//==============================================================================

#region libraries
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using TuringTrader.Simulator;
#endregion

namespace BooksAndPubs
{
    //----------
    #region MRx common core
    /// <summary>
    /// Mean-Reversion Strategy, common core for long and short
    /// </summary>
    public abstract class Bensdorp_30MinStockTrader_MRx_Core : Algorithm
    {
        #region inputs
        protected abstract List<string> UNIVERSE { get; }
        public abstract int ENTRY_DIR     { get; set; }
        public abstract int SMA_DAYS      { get; set; }
        public abstract int MIN_ADX       { get; set; }
        public abstract int MIN_ATR       { get; set; }
        public abstract int MINMAX_RSI    { get; set; }
        public abstract int STOP_LOSS     { get; set; }
        public abstract int PROFIT_TARGET { get; set; }
        public abstract int MAX_CAP       { get; set; }
        public abstract int MAX_RISK      { get; set; }
        public abstract int MAX_ENTRIES   { get; set; }
        public abstract int MAX_HOLD_DAYS { get; set; }
        #endregion
        #region internal data
        private static readonly string BENCHMARK = "$SPX.index";
        private static readonly double INITIAL_CAPITAL = 1e6;

        private Plotter _plotter = new Plotter();
        private Instrument _benchmark;
        #endregion

        #region public override void Run()
        public override void Run()
        {
            //========== initialization ==========

            StartTime = DateTime.Parse("01/01/2008");
            EndTime = DateTime.Now.Date - TimeSpan.FromDays(5);

            foreach (var n in UNIVERSE)
                AddDataSource(n);
            AddDataSource(BENCHMARK);

            Deposit(INITIAL_CAPITAL);
            CommissionPerShare = 0.015;

            var entryParameters = Enumerable.Empty<Instrument>()
                .ToDictionary(
                    i => i,
                    i => new
                    {
                        entryDate = default(DateTime),
                        entryPrice = default(double),
                        stopLoss = default(double),
                        profitTarget = default(double),
                    });

            //========== simulation loop ==========

            foreach (var s in SimTimes)
            {
                //----- find instruments

                _benchmark = _benchmark ?? FindInstrument(BENCHMARK);
                var universe = Instruments
                    .Where(i => i != _benchmark)
                     .ToList();

                //----- calculate indicators

                // make sure to calculate indicators for the 
                // full universe on every single bar
                var indicators = universe
                    .ToDictionary(
                        i => i,
                        i => new
                        {
                            sma150 = i.Close.SMA(SMA_DAYS),
                            adx7 = i.ADX(7),
                            atr10 = i.TrueRange().Divide(i.Close).SMA(10),
                            rsi3 = i.Close.RSI(3),
                        });

                // * daily close must be > 150-day SMA
                // * 7-day ADX > 45
                // * 10-day ATR % > 4 %
                // * 3-day RSI < 30
                var filtered = universe
                    .Where(i =>
                        ENTRY_DIR > 0
                            ? i.Close[0] > indicators[i].sma150[0] // long: above
                            : i.Close[0] < indicators[i].sma150[0] // short: below
                        && indicators[i].adx7[0] > MIN_ADX
                        && indicators[i].atr10[0] > MIN_ATR / 10000.0
                        && ENTRY_DIR > 0
                            ? indicators[i].rsi3[0] < MINMAX_RSI  // long: maximum
                            : indicators[i].rsi3[0] > MINMAX_RSI) // short: minimum
                    .ToList();

                //----- manage existing positions

                int numOpenPositions = Positions.Keys.Count();
                foreach (var pos in Positions.Keys)
                {
                    // time-based exit
                    if (entryParameters[pos].entryDate <= SimTime[MAX_HOLD_DAYS - 1])
                    {
                        pos.Trade(-pos.Position).Comment = "time exit";
                        numOpenPositions--;
                    }
                    else if (pos.Close[0] >= entryParameters[pos].profitTarget)
                    {
                        pos.Trade(-pos.Position,
                                OrderType.openNextBar)
                            .Comment = "profit target";
                        numOpenPositions--;
                    }
                    else
                    {
                        pos.Trade(-pos.Position,
                                OrderType.stopNextBar,
                                entryParameters[pos].stopLoss)
                            .Comment = "stop loss";
                    }
                }

                //----- open new positions

                if (NextSimTime.DayOfWeek < SimTime[0].DayOfWeek) // open positions on Monday
                {
                    // each week, sort all stocks that meet that criteria by RSI
                    // buy the 10 LOWEST RSI scores at the Monday open with 
                    // a LIMIT order 4 % BELOW the Friday close.
                    var entries = ENTRY_DIR > 0
                        ? filtered // long
                            .Where(i => i.Position == 0)
                            .OrderBy(i => indicators[i].rsi3[0])
                            .Take(MAX_ENTRIES - numOpenPositions)
                            .ToList()
                        : filtered // short
                            .Where(i => i.Position == 0)
                            .OrderByDescending(i => indicators[i].rsi3[0])
                            .Take(MAX_ENTRIES - numOpenPositions)
                            .ToList();

                    foreach (var i in entries)
                    {
                        // save our entry parameters, so that we may access
                        // them later to manage exits
                        double entryPrice = ENTRY_DIR > 0
                                ? i.Close[0] * (1.0 - MIN_ATR / 10000.0) // long
                                : i.Close[0];                            // short

                        double stopLoss = ENTRY_DIR > 0
                                ? entryPrice * (1.0 - STOP_LOSS / 100.0 * indicators[i].atr10[0])  // long
                                : entryPrice * (1.0 + STOP_LOSS / 100.0 * indicators[i].atr10[0]); // short

                        double profitTarget = ENTRY_DIR > 0
                            ? entryPrice * (1.0 + PROFIT_TARGET / 10000.0)  // long
                            : entryPrice * (1.0 - PROFIT_TARGET / 10000.0); // short

                        entryParameters[i] = new
                        {
                            entryDate = NextSimTime,
                            entryPrice = entryPrice,
                            stopLoss = stopLoss,
                            profitTarget = profitTarget,
                        };

                        // calculate target shares in two ways:
                        // * fixed-fractional risk (with entry - stop-loss = "risk"), and
                        // * fixed percentage of total equity
                        double riskPerShare = ENTRY_DIR > 0
                            ? Math.Max(0.10, entryPrice - stopLoss)  // long
                            : Math.Max(0.10, stopLoss - entryPrice); // short

                        int sharesRiskLimited = (int)Math.Floor(MAX_RISK / 100.0 / MAX_ENTRIES * NetAssetValue[0] / riskPerShare);
                        int sharesCapLimited = (int)Math.Floor(MAX_CAP / 100.0 / MAX_ENTRIES * NetAssetValue[0] / entryParameters[i].entryPrice);
                        int targetShares = (ENTRY_DIR > 0 ? 1 : -1) * Math.Min(sharesRiskLimited, sharesCapLimited);

                        // place trade as limit order
                        i.Trade(targetShares,
                            OrderType.limitNextBar,
                            entryParameters[i].entryPrice);
                    }
                }

                //----- output

                if (!IsOptimizing)
                {
                    // plot to chart
                    _plotter.SelectChart(Name + " " + OptimizerParamsAsString, "date");
                    _plotter.SetX(SimTime[0]);
                    _plotter.Plot("nav", NetAssetValue[0]);
                    _plotter.Plot(_benchmark.Symbol, _benchmark.Close[0]);

                    // placeholder, to make sure positions land on sheet 2
                    _plotter.SelectChart(Name + " positions", "entry date");

                    // additional indicators
                    _plotter.SelectChart(Name + " extra", "date");
                    _plotter.SetX(SimTime[0]);
                    _plotter.Plot("leverage", Instruments.Sum(i => i.Position * i.Close[0]) / NetAssetValue[0]);
                }
            }

            //========== post processing ==========

            //----- print position log, grouped as LIFO

            if (!IsOptimizing)
            {
                var tradeLog = LogAnalysis
                    .GroupPositions(Log, true)
                    .OrderBy(i => i.Entry.BarOfExecution.Time);

                _plotter.SelectChart(Name + " positions", "entry date");
                foreach (var trade in tradeLog)
                {
                    _plotter.SetX(trade.Entry.BarOfExecution.Time);
                    _plotter.Plot("exit date", trade.Exit.BarOfExecution.Time);
                    _plotter.Plot("Symbol", trade.Symbol);
                    _plotter.Plot("Quantity", trade.Quantity);
                    _plotter.Plot("% Profit", trade.Exit.FillPrice / trade.Entry.FillPrice - 1.0);
                    _plotter.Plot("Exit", trade.Exit.OrderTicket.Comment ?? "");
                    //_plotter.Plot("$ Profit", trade.Quantity * (trade.Exit.FillPrice - trade.Entry.FillPrice));
                }
            }

            //----- optimization objective

            double cagr = Math.Exp(252.0 / Math.Max(1, TradingDays) * Math.Log(NetAssetValue[0] / INITIAL_CAPITAL)) - 1.0;
            FitnessValue = cagr / Math.Max(1e-10, Math.Max(0.01, NetAssetValueMaxDrawdown));

            if (!IsOptimizing)
                Output.WriteLine("CAGR = {0:P2}, DD = {1:P2}, Fitness = {2:F4}", cagr, NetAssetValueMaxDrawdown, FitnessValue);
        }
        #endregion
        #region public override void Report()
        public override void Report()
        {
            _plotter.OpenWith("SimpleReport");
        }
        #endregion
    }
    #endregion
    #region MRL core
    public abstract class Bensdorp_30MinStockTrader_MRL_Core : Bensdorp_30MinStockTrader_MRx_Core
    {
        public override int ENTRY_DIR     { get; set; } = 1;   // 1 = long
        public override int SMA_DAYS      { get; set; } = 150; // 150 days
        public override int MIN_ADX       { get; set; } = 45;
        public override int MIN_ATR       { get; set; } = 400; // 4%
        public override int MINMAX_RSI    { get; set; } = 30;  // long: maximum
        public override int STOP_LOSS     { get; set; } = 250; // 2.5 x ATR
        public override int PROFIT_TARGET { get; set; } = 300; // 3%
        public override int MAX_CAP       { get; set; } = 100; // 100%
        public override int MAX_RISK      { get; set; } = 20;  // 20%
        public override int MAX_ENTRIES   { get; set; } = 10;  // 10
        public override int MAX_HOLD_DAYS { get; set; } = 4;   // 4 days
    }
    #endregion
    #region MRS core
    public abstract class Bensdorp_30MinStockTrader_MRS_Core : Bensdorp_30MinStockTrader_MRx_Core
    {
        public override int ENTRY_DIR     { get; set; } = -1;  // -1 = short
        public override int SMA_DAYS      { get; set; } = 150; // 150 days
        public override int MIN_ADX       { get; set; } = 50;
        public override int MIN_ATR       { get; set; } = 500; // 5%
        public override int MINMAX_RSI    { get; set; } = 85;  // short: minimum
        public override int STOP_LOSS     { get; set; } = 250; // 2.5 x ATR
        public override int PROFIT_TARGET { get; set; } = 400; // 4%
        public override int MAX_CAP       { get; set; } = 100; // 100%
        public override int MAX_RISK      { get; set; } = 20;  // 20%
        public override int MAX_ENTRIES   { get; set; } = 10;  // 10
        public override int MAX_HOLD_DAYS { get; set; } = 2;   // 2 days
    }
    #endregion

    //----------
    #region universes
    class Universes
    {
        public static List<string> NDX = new List<string>()
        {
            // Trade all US stocks, but filter out 
            // * ETFs; 
            // * stocks < $10; and 
            // * average daily volume< 500,000 over last 50 days.

            // here, we use Nasdaq 100, as of 03/21/2019
            "AAL.stock",
            "AAPL.stock",
            "ADBE.stock",
            "ADI.stock",
            "ADP.stock",
            "ADSK.stock",
            "ALGN.stock",
            "ALXN.stock",
            "AMAT.stock",
            "AMD.stock",
            "AMGN.stock",
            "AMZN.stock",
            "ASML.stock",
            "ATVI.stock",
            "AVGO.stock",
            "BIDU.stock",
            "BIIB.stock",
            "BKNG.stock",
            "BMRN.stock",
            "CDNS.stock",
            "CELG.stock",
            "CERN.stock",
            "CHKP.stock",
            "CHTR.stock",
            "CMCSA.stock",
            "COST.stock",
            "CSCO.stock",
            "CSX.stock",
            "CTAS.stock",
            "CTRP.stock",
            "CTSH.stock",
            "CTXS.stock",
            "DLTR.stock",
            "EA.stock",
            "EBAY.stock",
            "EXPE.stock",
            "FAST.stock",
            "FB.stock",
            "FISV.stock",
            "GILD.stock",
            "GOOG.stock",
            "GOOGL.stock",
            "HAS.stock",
            "HSIC.stock",
            "IDXX.stock",
            "ILMN.stock",
            "INCY.stock",
            "INTC.stock",
            "INTU.stock",
            "ISRG.stock",
            "JBHT.stock",
            "JD.stock",
            "KHC.stock",
            "KLAC.stock",
            "LBTYA.stock",
            "LBTYK.stock",
            "LRCX.stock",
            "LULU.stock",
            "MAR.stock",
            "MCHP.stock",
            "MDLZ.stock",
            "MELI.stock",
            "MNST.stock",
            "MSFT.stock",
            "MU.stock",
            "MXIM.stock",
            "MYL.stock",
            "NFLX.stock",
            "NTAP.stock",
            "NTES.stock",
            "NVDA.stock",
            "NXPI.stock",
            "ORLY.stock",
            "PAYX.stock",
            "PCAR.stock",
            "PEP.stock",
            "PYPL.stock",
            "QCOM.stock",
            "REGN.stock",
            "ROST.stock",
            "SBUX.stock",
            "SIRI.stock",
            "SNPS.stock",
            "SWKS.stock",
            "SYMC.stock",
            "TFCF.stock.x",
            "TFCFA.stock.x",
            "TMUS.stock",
            "TSLA.stock",
            "TTWO.stock",
            "TXN.stock",
            "UAL.stock",
            "ULTA.stock",
            "VRSK.stock",
            "VRSN.stock",
            "VRTX.stock",
            "WBA.stock",
            "WDAY.stock",
            "WDC.stock",
            "WLTW.stock",
            "WYNN.stock",
            "XEL.stock",
            "XLNX.stock",
        };
        public static List<string> OEX = new List<string>()
        {
            // Trade all US stocks, but filter out 
            // * ETFs; 
            // * stocks < $10; and 
            // * average daily volume< 500,000 over last 50 days.

            // here, we use S&P 100, as of 03/20/2019
            "AAPL.stock",
            "ABBV.stock",
            "ABT.stock",
            "ACN.stock",
            "ADBE.stock",
            "AGN.stock",
            "AIG.stock",
            "ALL.stock",
            "AMGN.stock",
            "AMZN.stock",
            "AXP.stock",
            "BA.stock",
            "BAC.stock",
            "BIIB.stock",
            "BK.stock",
            "BKNG.stock",
            "BLK.stock",
            "BMY.stock",
            "BRK.B.stock",
            "C.stock",
            "CAT.stock",
            "CELG.stock",
            "CHTR.stock",
            "CL.stock",
            "CMCSA.stock",
            "COF.stock",
            "COP.stock",
            "COST.stock",
            "CSCO.stock",
            "CVS.stock",
            "CVX.stock",
            "DHR.stock",
            "DIS.stock",
            "DUK.stock",
            "DWDP.stock",
            "EMR.stock",
            "EXC.stock",
            "F.stock",
            "FB.stock",
            "FDX.stock",
            "GD.stock",
            "GE.stock",
            "GILD.stock",
            "GM.stock",
            "GOOG.stock",
            "GOOGL.stock",
            "GS.stock",
            "HAL.stock",
            "HD.stock",
            "HON.stock",
            "IBM.stock",
            "INTC.stock",
            "JNJ.stock",
            "JPM.stock",
            "KHC.stock",
            "KMI.stock",
            "KO.stock",
            "LLY.stock",
            "LMT.stock",
            "LOW.stock",
            "MA.stock",
            "MCD.stock",
            "MDLZ.stock",
            "MDT.stock",
            "MET.stock",
            "MMM.stock",
            "MO.stock",
            "MRK.stock",
            "MS.stock",
            "MSFT.stock",
            "NEE.stock",
            "NFLX.stock",
            "NKE.stock",
            "NVDA.stock",
            "ORCL.stock",
            "OXY.stock",
            "PEP.stock",
            "PFE.stock",
            "PG.stock",
            "PM.stock",
            "PYPL.stock",
            "QCOM.stock",
            "RTN.stock",
            "SBUX.stock",
            "SLB.stock",
            "SO.stock",
            "SPG.stock",
            "T.stock",
            "TGT.stock",
            "TXN.stock",
            "UNH.stock",
            "UNP.stock",
            "UPS.stock",
            "USB.stock",
            "UTX.stock",
            "V.stock",
            "VZ.stock",
            "WBA.stock",
            "WFC.stock",
            "WMT.stock",
            "XOM.stock",
        };
    }
    #endregion
    #region MRL (NDX + OEX)
    public class Bensdorp_30MinStockTrader_MRL_NDX_OEX : Bensdorp_30MinStockTrader_MRL_Core
    {
        protected override List<string> UNIVERSE
        {
            get
            {
                return Universes.NDX
                    .Concat(Universes.OEX)
                    .ToList();
            }
        }
    }
    #endregion
    #region MRS (NDX + OEX)
    public class Bensdorp_30MinStockTrader_SRL_NDX_OEX : Bensdorp_30MinStockTrader_MRS_Core
    {
        protected override List<string> UNIVERSE
        {
            get
            {
                return Universes.NDX
                    .Concat(Universes.OEX)
                    .ToList();
            }
        }
    }
    #endregion
}

//==============================================================================
// end of file