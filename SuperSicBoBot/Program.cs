using System.Globalization;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace DiceWebSocketBot
{

    public class DiceGameClient
    {
        private static readonly Dictionary<string, int> DiceMap = new Dictionary<string, int>
        {
            { "⚀", 1 }, { "⚁", 2 }, { "⚂", 3 }, { "⚃", 4 }, { "⚄", 5 }, { "⚅", 6 }
        };

        // Game state fields
        private string _lastGameTime = string.Empty;
        private int _roundsCountTocheck = 5;
        private string _lastGameId = string.Empty;
        private int _lastBestNumberToBet = -1;
        private int _retryBetCount = 0;
        private int _maxRetry = 2;
        private int _betAmount = 200;
        private double current_balance = 0;
        private double first_balance = 0;
        private bool _isFirstTimeGetBalance = true;


        public DiceGameClient(int betAmount, int roundsCountTocheck)
        {
            _betAmount = betAmount;
            _roundsCountTocheck = roundsCountTocheck;
        }

        public async Task RunAsync(string websocketUrl, CancellationToken cancellationToken = default)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    await ConnectAndListenAsync(websocketUrl, cancellationToken);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Error: {ex.Message}");
                }

                Console.WriteLine("Connection closed. Reconnecting in 3 seconds...");
                await Task.Delay(TimeSpan.FromSeconds(3), cancellationToken);
            }
        }

        private async Task ConnectAndListenAsync(string websocketUrl, CancellationToken cancellationToken)
        {
            using (var webSocket = new ClientWebSocket())
            {
                await webSocket.ConnectAsync(new Uri(websocketUrl), cancellationToken);
                Console.WriteLine("Connected to WebSocket");

                var buffer = new byte[8192];
                while (webSocket.State == WebSocketState.Open && !cancellationToken.IsCancellationRequested)
                {
                    var result = await webSocket.ReceiveAsync(new ArraySegment<byte>(buffer), cancellationToken);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        await webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, string.Empty, cancellationToken);
                        break;
                    }

                    if (result.MessageType == WebSocketMessageType.Text)
                    {
                        string message = Encoding.UTF8.GetString(buffer, 0, result.Count);
                        await ProcessMessageAsync(message, webSocket, cancellationToken);
                    }
                }
            }
        }

        private async Task ProcessMessageAsync(string message, ClientWebSocket webSocket, CancellationToken cancellationToken)
        {
            try
            {
                using (JsonDocument document = JsonDocument.Parse(message))
                {
                    bool hide = false;
                    JsonElement root = document.RootElement;

                    if (root.TryGetProperty("type", out JsonElement dtypeElement) &&
                        dtypeElement.GetString() == "balanceUpdated" &&
                        root.TryGetProperty("args", out JsonElement ddargsElement))
                    {
                        if (ddargsElement.TryGetProperty("balance", out JsonElement balanceElement) &&
                            balanceElement.TryGetDouble(out double balance))
                        {
                            if (_isFirstTimeGetBalance)
                            {
                                first_balance = balance;
                            }
                            current_balance = balance;
                            _isFirstTimeGetBalance = false;

                            Console.ForegroundColor = ConsoleColor.Magenta;
                            Console.WriteLine($" - Balance : {current_balance} | Win or Lose : {current_balance - first_balance}");
                            Console.ResetColor();
                        }
                    }

                    if (root.TryGetProperty("type", out JsonElement typeElement) &&
                    typeElement.GetString() == "dice.state" &&
                    root.TryGetProperty("args", out JsonElement argsElement))
                    {
                        var _tempLastGameTime = _lastGameTime;
                        UpdateGameState(argsElement);
                        if (_tempLastGameTime == _lastGameTime)
                            hide = true;
                        var roundsResults = ParseRecentResults(argsElement);

                        if (roundsResults.Count > 0)
                        {
                            var firstRound = roundsResults[0];
                            Console.ForegroundColor = ConsoleColor.Blue;
                            Console.WriteLine($" - Last Rounds : {firstRound["1"]}/{firstRound["2"]}/{firstRound["3"]}");
                            Console.WriteLine($" - Game Time : {_lastGameTime}");
                            Console.ResetColor();

                            int roundsCount = Math.Min(_roundsCountTocheck, roundsResults.Count);
                            int bestNumber = FindBestNumberToBet(roundsResults.GetRange(0, roundsCount));

                            var lastRoundNumbers = new List<int>
                            {
                                firstRound["1"],
                                firstRound["2"],
                                firstRound["3"]
                            };

                            Console.ForegroundColor = ConsoleColor.Yellow;
                            Console.WriteLine(bestNumber > 0 ? $" - Best game is: {bestNumber}" : " - No Number To Bet");
                            Console.ResetColor();



                            if (bestNumber != 0)
                            {
                                if (lastRoundNumbers.Contains(bestNumber))
                                {
                                    _retryBetCount = 0;
                                    _lastBestNumberToBet = -1;
                                }

                                if (_lastBestNumberToBet != bestNumber)
                                {
                                    _retryBetCount = 0;
                                }

                                if (IsGameTimeValid(_lastGameTime))
                                {
                                    if (_retryBetCount >= _maxRetry)
                                    {
                                        if (!hide)
                                            Console.WriteLine(" - No Bet Times have been left !");
                                        await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
                                        return;
                                    }

                                    if (_lastBestNumberToBet == bestNumber && _retryBetCount >= _maxRetry)
                                    {
                                        await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
                                        return;
                                    }

                                    if(_tempLastGameTime != _lastGameTime)
                                    {
                                        await SendBetAsync(webSocket, bestNumber, cancellationToken);
                                    }
                                }
                                else
                                {
                                    return;
                                    //Console.WriteLine("-- Round Time Diff, Please wait for the next round");
                                }
                            }
                        }
                    }
                }
            }
            catch (JsonException jsonEx)
            {
                Console.WriteLine("JSON parsing error: " + jsonEx.Message);
            }
            catch (Exception ex)
            {
                Console.WriteLine("Error processing message: " + ex.Message);
            }
            var titleUpdateTask = Task.Run(() => UpdateTitleAsync(cancellationToken), cancellationToken);
        }

        private void UpdateGameState(JsonElement argsElement)
        {
            if (argsElement.TryGetProperty("number", out JsonElement numberElement))
            {
                _lastGameTime = numberElement.GetString() ?? string.Empty;
            }

            if (argsElement.TryGetProperty("gameId", out JsonElement gameIdElement))
            {
                _lastGameId = gameIdElement.GetString() ?? string.Empty;
            }
        }

        private List<Dictionary<string, int>> ParseRecentResults(JsonElement argsElement)
        {
            var roundsResults = new List<Dictionary<string, int>>();
            if (argsElement.TryGetProperty("recentResults", out JsonElement recentResultsElement) &&
                recentResultsElement.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement round in recentResultsElement.EnumerateArray())
                {
                    if (round.ValueKind == JsonValueKind.String)
                    {
                        string roundStr = round.GetString() ?? string.Empty;
                        if (roundStr.Length >= 3)
                        {
                            var formattedResult = new Dictionary<string, int>();
                            // Extract first three characters as dice results
                            string dice1 = roundStr[0].ToString();
                            string dice2 = roundStr[1].ToString();
                            string dice3 = roundStr[2].ToString();

                            if (DiceMap.TryGetValue(dice1, out int value1) &&
                                DiceMap.TryGetValue(dice2, out int value2) &&
                                DiceMap.TryGetValue(dice3, out int value3))
                            {
                                formattedResult["1"] = value1;
                                formattedResult["2"] = value2;
                                formattedResult["3"] = value3;
                                roundsResults.Add(formattedResult);
                            }
                        }
                    }
                }
            }
            return roundsResults;
        }

        private int FindBestNumberToBet(List<Dictionary<string, int>> roundsResults)
        {
            var allValues = new List<int>();
            foreach (var round in roundsResults)
            {
                allValues.AddRange(round.Values);
            }

            allValues.Sort();

            for (int i = 1; i <= 6; i++)
            {
                if (!allValues.Contains(i))
                {
                    return i;
                }
            }
            return 0; // Indicates no missing number found
        }
        private int GetBetAmoutByCount(int count)
        {
            var c = count + 1;
            return _betAmount * c;
        }

        private async Task SendBetAsync(ClientWebSocket webSocket, int bestNumber, CancellationToken cancellationToken)
        {
            var _betAmountTemp = GetBetAmoutByCount(_retryBetCount);
            var payloadChip = new
            {
                log = new
                {
                    type = "CLIENT_BET_CHIP",
                    value = new
                    {
                        type = "Chip",
                        codes = new Dictionary<string, int>
                        {
                            { $"SicBo_{bestNumber}", _betAmountTemp }
                        },
                        amount = _betAmountTemp,
                        currency = "IQD",
                        chipStack = new int[] { 200, 500, 1000, 2000, 5000, 25000 },
                        gameType = "sicbo",
                        tableMinLimit = 200,
                        tableMaxLimit = 2500000,
                        bets = new Dictionary<string, int>
                        {
                            { $"SicBo_{bestNumber}", _betAmountTemp }
                        },
                        gameTime = _lastGameTime,
                        channel = "PCMac",
                        orientation = "landscape",
                        gameDimensions = new
                        {
                            width = 1122,
                            height = 631.125
                        },
                        gameId = _lastGameId
                    }
                }
            };

            var payloadBetAction = new
            {
                id = "alevmddm5e",
                type = "dice.betAction",
                args = new
                {
                    gameId = _lastGameId,
                    type = "SET_CHIPS",
                    chips = new Dictionary<string, int>
                    {
                        { ReturnNumberBox(bestNumber), _betAmountTemp }
                    },
                    betTags = new
                    {
                        mwLayout = 8,
                        openMwTables = 1,
                        btVideoQuality = "_hd",
                        btTableView = "1",
                        orientation = "landscape",
                        videoProtocol = "fmp4"
                    }
                }
            };

            string jsonChip = JsonSerializer.Serialize(payloadChip);
            string jsonBetAction = JsonSerializer.Serialize(payloadBetAction);

            await SendMessageAsync(webSocket, jsonChip, cancellationToken);
            await SendMessageAsync(webSocket, jsonBetAction, cancellationToken);

            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine($" - Sent bet request: {_betAmountTemp} IQD");
            Console.WriteLine(" - Sleeping for 6 seconds ...");
            Console.ResetColor();
            _lastBestNumberToBet = bestNumber;
            _retryBetCount += 1;
            await Task.Delay(TimeSpan.FromSeconds(9), cancellationToken);
        }

        private async Task SendMessageAsync(ClientWebSocket webSocket, string message, CancellationToken cancellationToken)
        {
            byte[] messageBytes = Encoding.UTF8.GetBytes(message);
            await webSocket.SendAsync(new ArraySegment<byte>(messageBytes), WebSocketMessageType.Text, true, cancellationToken);
        }

        private bool IsGameTimeValid(string gameTimeStr, int toleranceInSeconds = 8)
        {
            if (DateTime.TryParseExact(gameTimeStr, "HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime gameTime))
            {
                DateTime now = DateTime.UtcNow;
                // Set the game time's date to today.
                gameTime = new DateTime(now.Year, now.Month, now.Day, gameTime.Hour, gameTime.Minute, gameTime.Second);
                // Adjust the game time by adding 3 hours.
                DateTime adjustedTime = gameTime;
                double diffSeconds = Math.Abs((now - adjustedTime).TotalSeconds);
                return diffSeconds <= toleranceInSeconds;
            }
            throw new FormatException("Invalid time format. Please use HH:mm:ss.");
        }

        private string ReturnNumberBox(int number)
        {
            return number switch
            {
                1 => "⚀",
                2 => "⚁",
                3 => "⚂",
                4 => "⚃",
                5 => "⚄",
                6 => "⚅",
                _ => string.Empty,
            };
        }

        private async Task UpdateTitleAsync(CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                // Update the console title using the current state.
                Console.Title = $"Retry Counts : {_retryBetCount} | Def Bet Amount {_betAmount} | Now bet Amount {GetBetAmoutByCount(_retryBetCount)} | Betting On {_lastBestNumberToBet}";
                await Task.Delay(1000, cancellationToken); // Adjust delay as needed.
            }
        }
    }

    internal class Program
    {
        private static async Task<bool> CheckActivationAsync()
        {
            const string activationUrl = "https://pastebin.com/raw/nqbxtPFp";

            using (var client = new HttpClient())
            {
                try
                {
                    string response = await client.GetStringAsync(activationUrl);
                    return response.Trim() == "1";
                }
                catch (Exception ex)
                {
                    Console.WriteLine("Activation check failed 0w0");
                    return false;
                }
            }
        }

        private static async Task Main(string[] args)
        {
            bool isActivated = await CheckActivationAsync();
            if (!isActivated)
            {
                Console.WriteLine("The application is not activated. Exiting in 10 seconds...");
                await Task.Delay(10000);
                Environment.Exit(0);
            }
            Console.WriteLine("Activation confirmed. Starting ...\n\n");

            Console.Write("Enter WSS URL: ");
            string websocketUrl = Console.ReadLine();

            int betAmount = PromptBetAmount();
            int roundsCountTocheck = PromptSearchRows();

            var diceGameClient = new DiceGameClient(betAmount, roundsCountTocheck);
            using (var cancellationSource = new CancellationTokenSource())
            {
                Console.CancelKeyPress += (sender, eventArgs) =>
                {
                    eventArgs.Cancel = true;
                    cancellationSource.Cancel();
                };

                await diceGameClient.RunAsync(websocketUrl, cancellationSource.Token);
            }
        }

        private static int PromptBetAmount()
        {
            Console.Write("Bet Amount (200,500,1000,2000) def is 200: ");
            string betAmountInput = Console.ReadLine();
            if (int.TryParse(betAmountInput, out int betAmount) && (betAmount == 200 || betAmount == 500 || betAmount == 1000 || betAmount == 2000))
            {
                return betAmount;
            }

            Console.WriteLine("Invalid bet amount entered. Using default bet amount (200).");
            return 200;
        }

        private static int PromptSearchRows()
        {
            Console.Write("Search Row Count (def 5): ");
            string roundsCountTocheck = Console.ReadLine();
            if (int.TryParse(roundsCountTocheck, out int _roundsCountTocheck) && (_roundsCountTocheck == 1 
                || _roundsCountTocheck == 1 
                || _roundsCountTocheck == 2 
                || _roundsCountTocheck == 3
                || _roundsCountTocheck == 5
                || _roundsCountTocheck == 6
                || _roundsCountTocheck == 7
                || _roundsCountTocheck == 8
                || _roundsCountTocheck == 9
                || _roundsCountTocheck == 10))
            {
                return _roundsCountTocheck;
            }

            Console.WriteLine("Invalid search in rows counts entered. Using default row search (5).");
            return 5;
        }
    }
}
