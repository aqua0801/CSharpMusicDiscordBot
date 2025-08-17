using System.Collections.Concurrent;
using System.Text;
using NetMQ;
using NetMQ.Sockets;

namespace DiscordBot
{
    public static class LanguageModelCore
    {
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
        }
        private const string _ip = "127.0.0.1" , _port = "5555" , _protocol = "tcp";
        private const int _maxToken = 1000 , _maxPromptLength = _maxToken * 4;
        private static ConcurrentDictionary<ulong, List<PromptResponse>> _channelPromptResponseMap = new ConcurrentDictionary<ulong, List<PromptResponse>>();
        public static string GetModelResponse(string prompt , ulong uid)
        {
            string response = "hello world !";

            string mergedPrompt = LanguageModelCore.GetMergedPrompt(uid,prompt);

            using (var requester = new RequestSocket())
            {
                requester.Connect($"{LanguageModelCore._protocol}://{LanguageModelCore._ip}:{LanguageModelCore._port}");
                requester.SendFrame(mergedPrompt);
                response = requester.ReceiveFrameString();
            }

            LanguageModelCore.StorePromptContext(uid,prompt,response);

            return response;
        }

        private static string GetMergedPrompt(ulong uid , string newPrompt)
        {
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
