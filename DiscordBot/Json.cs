using DiscordBot;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection.Metadata;

namespace DiscordBot
{
    public class Json
    {
        public static JWriter JsonWriter = new JWriter();

        public class JWriter
        {
            private bool blnWriting = false;
            private ConcurrentQueue<Tuple<string, string>> JsonWriteQueue = new ConcurrentQueue<Tuple<string, string>>();


            public void Append(string FilePath, string JsonString)
            {
                this.JsonWriteQueue.Enqueue(Tuple.Create(FilePath, JsonString));
            }

            public void Write()
            {

                if (this.JsonWriteQueue.Count() < 1) return;
                if (this.blnWriting) return;

                this.blnWriting = true;
                lock (this.JsonWriteQueue)
                {
                    for (int index = 0; index < this.JsonWriteQueue.Count(); index++)
                    {
                        if(this.JsonWriteQueue.TryDequeue(out var WriteQueue))
                        {
                            File.WriteAllText(WriteQueue.Item1, WriteQueue.Item2);
                        }
                    }
                }
                this.blnWriting = false;
            }
        }
        private static string GetFilePath(string filename)
        {
            return Path.Combine(Directory.GetCurrentDirectory(), filename);
        }


        public static JObject Read(string JsonFileName)
        {

            if (!JsonFileName.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) JsonFileName += ".json";
            string FullFileDirectory = GetFilePath(JsonFileName);
            if (!File.Exists(FullFileDirectory)) throw new FileNotFoundException("File not found", FullFileDirectory);
            JObject JsonObject = JObject.Parse(File.ReadAllText(FullFileDirectory));
            return JsonObject;
        }

        public static void Write(JObject JsonObject, string FileName)
        {
            if (!FileName.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) FileName += ".json";
            string JsonString = JsonObject.ToString(Newtonsoft.Json.Formatting.Indented);
            string FullFilename = GetFilePath(FileName);


            Json.JsonWriter.Append(FullFilename, JsonString);
  
            Json.JsonWriter.Write();
        }

        public static JObject ConvertDictionaryToJObject<K, V>(Dictionary<K, V> TargetDict)
        {
            var JTemp = new JObject();
            foreach (KeyValuePair<K, V> kvp in TargetDict)
            {
                JTemp[kvp.Key] = JToken.FromObject(kvp.Value);
            }
            return JTemp;
        }

        public static void JsonParseErrorLog(string LogString, string JsonParsePath, string Keys, string OtherString = "")
        {
            string ErrorLog = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}]{LogString}{Environment.NewLine}" +
                  $"Json解析路徑 : {JsonParsePath}{Environment.NewLine}" +
                  $"關鍵字 : {Keys}";
            if (!string.IsNullOrWhiteSpace(OtherString)) ErrorLog += $"{Environment.NewLine}{OtherString}";
            ErrorLog += Environment.NewLine;
        }


    }

    internal static class JsonObjectExtensions
    {
        public static T GetValueOrDefault<T>(this JToken token, params string[] keysAndIndexes)
        {
            foreach (var keyOrIndex in keysAndIndexes)
            {

                if (token is JArray arr)
                {
                    token = arr.GetValueOrDefault<JToken>(keyOrIndex);
                }
                else if (token is JObject obj)
                {
                    token = obj.GetValueOrDefault<JToken>(keyOrIndex);
                }
                else
                {
                    throw new InvalidDataException("Invalid jtoken type !");
                }
            }

            return token.ToObject<T>();
        }


        public static T GetValueOrDefault<T>(this JObject jobject, params string[] keys)
        {

            string ParsePath = "";
    
            try
            {
                JToken jtoken = jobject;
                ParsePath = jtoken.Path;

                foreach (var key in keys)
                {
                    if (jtoken is JObject obj && obj.TryGetValue(key, out JToken nextToken))
                    {
                        jtoken = nextToken;
                        ParsePath = jtoken.Path;
                    }
                    else
                    {
                        Json.JsonParseErrorLog("Json變數載入失敗 : 索引關鍵字不存在或是巢狀結構無效",  ParsePath, String.Join(" -> ", keys));
                        return default;
                    }
                }

                if (jtoken.Type != JTokenType.Null)
                {
                    return jtoken.ToObject<T>();
                }
                else if (Utils.IsNullableType<T>(StrictNullable: false))
                {
                    return default;
                }
                Json.JsonParseErrorLog("Json變數載入失敗 : JToken為空" , ParsePath, String.Join(" -> ", keys));
            }
            catch (Exception e)
            {
                Json.JsonParseErrorLog("Json變數載入失敗 : Exception thrown" , ParsePath, String.Join(" -> ", keys), e.ToString());
            }

            return default;
        }
        public static T GetValueOrDefault<T>(this JArray arr, string index)
        {
            if (int.TryParse(index, out int indexTemp)) return arr.GetValueOrDefault<T>(indexTemp);
            else throw new InvalidCastException("Invalid type cast from string to int !");
        }


        public static T GetValueOrDefault<T>(this JArray arr, int index)
        {
            try
            {
                T targetValue = arr[index].ToObject<T>();
                return targetValue;
            }
            catch (Exception e)
            {
                Json.JsonParseErrorLog("JArray indexing failed : Exception thrown", arr.Path, index.ToString(), e.ToString());
            }
            return default;
        }

        public static List<string> GetKeys(this JToken jtoken)
        {
            if (jtoken is JObject obj)
            {
                return obj.Properties().Select(prop => prop.Name).ToList();
            }
            else if (jtoken is JArray arr)
            {
                return Utils.Range(arr.Count).Select(index => index.ToString()).ToList();
            }

            return new List<string>();
        }
    }

}
