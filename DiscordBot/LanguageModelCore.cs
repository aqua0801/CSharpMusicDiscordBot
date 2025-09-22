using AngleSharp.Dom;
using AngleSharp.Text;
using NetMQ;
using NetMQ.Sockets;
using System.Collections.Concurrent;
using System.Net.Http;
using System.Text;
using System.Text.Json;


namespace DiscordBot
{
    public class ThinkingResponse
    {
        public string Tokens { get; set; }
        public string Thinking { get; set; }
        public string Response { get; set; }
    }
    public static class LanguageModelCore
    {
        private static List<Dictionary<string, string>> ToMessages(this List<PromptResponse> prompts)
        {
            //sys prompt is set 
            var messages = new List<Dictionary<string, string>>();
            foreach(var prompt in prompts)
            {
                messages.AddRange(prompt.ToContent());
            }
            return messages;
        }
        private record PromptResponse
        {
            public string Prompt { get; set; } = "";
            public string Response { get; set; } = "";

            public string GetMergedPrompt()
            {
                return $"User : {this.Prompt} , Response : {this.Response}";
            }

            public int GetTotalLength()
            {
                return this.GetMergedPrompt().Length;
            }

            public Dictionary<string, string>[] ToContent()
                => new Dictionary<string, string>[]
                {
                    new (){ {"role","user"} , {"content",this.Prompt} },
                    new (){ {"role", "assistant" } , {"content",this.Response} }
                };
        }

        private const string _ip = "127.0.0.1" , _port = "5555" , _protocol = "tcp";
        private const int _maxToken = 1000 , _maxPromptLength = _maxToken * 4;

        private static ConcurrentDictionary<ulong, List<PromptResponse>> _channelPromptResponseMap = new ConcurrentDictionary<ulong, List<PromptResponse>>();

        public static bool _cacheChatHistory = false;

        public static string GetModelResponse(string prompt , ulong uid)
        {
            string response = "hello world !";
           
            string mergedPrompt = LanguageModelCore.GetMergedPrompt(uid, prompt);

            response = SendByZmq(mergedPrompt);

            LanguageModelCore.StorePromptContext(uid, prompt, response);
            
            return response;
        }

        public static ThinkingResponse GetModelResponseToken(string prompt, ulong uid)
        {
            var result = new ThinkingResponse();

            _ = Task.Run(async () =>
            {
                var payload = GetPayload(uid, prompt);
                string jsonPayload = JsonSerializer.Serialize(payload);

                StringBuilder fullResponse = new StringBuilder();

                await foreach (var token in SendByHttp(jsonPayload))
                {
                    fullResponse.Append(token);
                    result.Tokens = fullResponse.ToString(); 
                }

                string response = fullResponse.ToString();

                if (response.Contains("</think>"))
                {
                    var parts = response.Split("</think>", 2);
                    result.Thinking = parts[0];
                    result.Thinking = result.Thinking.ReplaceFirst("<think>","");
                    result.Response = parts[1];
                }
                else
                {
                    result.Response = response;
                }

                StorePromptContext(uid, prompt, result.Response);
            });

            return result; // immediately return reference
        }



        private static string SendByZmq(string prompt)
        {
            using (var requester = new RequestSocket())
            {
                requester.Connect($"{LanguageModelCore._protocol}://{LanguageModelCore._ip}:{LanguageModelCore._port}");
                requester.SendFrame(prompt);
                return requester.ReceiveFrameString();
            }
        }

        private static async IAsyncEnumerable<string> SendByHttp(string payloadstring)
        {
            using HttpClient httpClient = new HttpClient();
            string url = "http://127.0.0.1:1234/v1/chat/completions";

            using var request = new HttpRequestMessage(HttpMethod.Post, url);
            request.Headers.Add("Accept", "application/json");
            request.Content = new StringContent(payloadstring, Encoding.UTF8, "application/json");

            using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
            response.EnsureSuccessStatusCode();

            using var stream = await response.Content.ReadAsStreamAsync();
            using var reader = new StreamReader(stream);

            while (!reader.EndOfStream)
            {
                var line = await reader.ReadLineAsync();
                if (string.IsNullOrWhiteSpace(line)) continue;

                if (line.StartsWith("data:"))
                {
                    var jsonDataStr = line.Substring("data:".Length).Trim();

                    if (jsonDataStr == "[DONE]")
                        yield break;

                    using var doc = JsonDocument.Parse(jsonDataStr);
                    var root = doc.RootElement;

                    if (root.TryGetProperty("choices", out var choices))
                    {
                        var delta = choices[0].GetProperty("delta");
                        if (delta.TryGetProperty("content", out var content))
                        {
                            yield return content.GetString()!;
                        }
                    }
                }
            }
        }
        private static object GetPayload(ulong uid, string newPrompt, string model = "qwen3-4b-thinking")
        {
            model = "baidu/ernie-4.5-21b-a3b";
            // If no history caching, just send the new prompt as a user message
            if (!LanguageModelCore._cacheChatHistory)
            {
                var messages = new List<Dictionary<string, string>>
                {
                    new() { ["role"] = "user", ["content"] = newPrompt }
                };

                return new
                {
                    model,
                    messages,
                    temperature = 0.7,
                    max_tokens = -1,
                    stream = true
                };
            }

            if (!LanguageModelCore._channelPromptResponseMap.TryGetValue(uid, out var prompts))
            {
                // No history yet, same as above
                var messages = new List<Dictionary<string, string>>
                {
                    new() { ["role"] = "user", ["content"] = newPrompt }
                };

                return new
                {
                    model,
                    messages,
                    temperature = 0.7,
                    max_tokens = -1,
                    stream = true
                };
            }

            // Convert history to messages
            var historyMessages = prompts.ToMessages(); // Using your extension method

            // Add the new prompt as latest user message
            historyMessages.Add(new Dictionary<string, string> { ["role"] = "user", ["content"] = newPrompt });

            return new
            {
                model,
                messages = historyMessages,
                temperature = 0.7,
                max_tokens = -1,
                stream = true
            };
        }

        private static string GetMergedPrompt(ulong uid , string newPrompt)
        {
            if (!LanguageModelCore._cacheChatHistory)
                return newPrompt;
            if (LanguageModelCore._channelPromptResponseMap.TryGetValue(uid, out var prompts))
            {
                StringBuilder promptBuilder = new StringBuilder();

                promptBuilder.AppendLine("Chat History : ");

                foreach (var prompt in prompts)
                    promptBuilder.AppendLine(prompt.GetMergedPrompt());

                promptBuilder.AppendLine("New prompt : ");
                promptBuilder.AppendLine(newPrompt);

                return promptBuilder.ToString();
            }
            else
                return newPrompt;
        }

        private static void StorePromptContext(ulong uid , string prompt , string response)
        {
            if (!LanguageModelCore._cacheChatHistory)
                return;
            lock (LanguageModelCore._channelPromptResponseMap)
            {
                if (!LanguageModelCore._channelPromptResponseMap.ContainsKey(uid))
                    LanguageModelCore._channelPromptResponseMap[uid] = new List<PromptResponse>();

                LanguageModelCore._channelPromptResponseMap[uid].Add
                    (
                        new PromptResponse { Prompt = prompt, Response = response }
                    );

                int totalLength = LanguageModelCore._channelPromptResponseMap[uid].Sum(prompt => prompt.GetTotalLength());

                while(totalLength > LanguageModelCore._maxPromptLength)
                {
                    totalLength -= LanguageModelCore._channelPromptResponseMap[uid].First().GetTotalLength();
                    LanguageModelCore._channelPromptResponseMap [uid].RemoveAt (0);
                }
            }
        }

        public static void ClearChatHistory(ulong uid)
        {
            LanguageModelCore._channelPromptResponseMap.TryRemove(uid,out var _);
        }


    }
}
