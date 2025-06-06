﻿using Discord;
using Discord.Interactions;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;

namespace DiscordBot
{

    public class ConfidenceResult
    {
        public float  Score { get; set; }
        public string Path {  get; set; }
        public string DisplayName { get; set;}
    }
    public class ImageAlgorithm
    {
        private ConcurrentDictionary<string,Tuple<string,TimeSpan>> _cachedQueryResult = new ConcurrentDictionary<string, Tuple<string, TimeSpan>>();
        internal class LabelDecoder
        {
            private const float _mainWeight = 1.8f;
            private const float _characWeight = 1.3f;
            private const float _sideWeight = 1.0f;
            private const float _equalWeight = 1.0f;
            public static (float,string) ToConfidence(JObject jobj,string query)
            {
                string[] mainTags = jobj.GetValueOrDefault<string[]>("main-tag");
                string[] characTags = jobj.GetValueOrDefault<string[]>("character-tag");
                string[] sideTags = jobj.GetValueOrDefault<string[]>("side-tag");

                float SegmentConfidence(string[] tags , float weight)
                {
                    double _sum=0;

                    foreach(string tag in tags)
                    {
                        float _weight = weight;
                        double _confidence = Utils.LevenshteinSimilarity(query,tag);
                        if (query == tag) _weight += LabelDecoder._equalWeight;
                        _sum += _weight * _confidence;
                    }
                    return (float)_sum;
                }

                return (SegmentConfidence(mainTags, LabelDecoder._mainWeight)
                    + SegmentConfidence(characTags, LabelDecoder._characWeight)
                    + SegmentConfidence(sideTags, LabelDecoder._sideWeight), mainTags[0]);
            }

        }

        private class MinHeapComparer : IComparer<ConfidenceResult>
        {
            public int Compare(ConfidenceResult x, ConfidenceResult y)
            {
                return x.Score.CompareTo(y.Score); // Min-heap based on Score
            }
        }
        private class HeapComparer : IComparer<(ConfidenceResult Item, int UniqueId)>
        {
            public int Compare((ConfidenceResult Item, int UniqueId) x, (ConfidenceResult Item, int UniqueId) y)
            {
                int cmp = x.Item.Score.CompareTo(y.Item.Score);
                if (cmp == 0)
                    return x.UniqueId.CompareTo(y.UniqueId); // Ensure uniqueness
                return cmp;
            }
        }
        private const string _imagesPath = GlobalVariable.imagesFolderPath;
        private const string _labelsPath = GlobalVariable.labelsFolderPath;


        public static List<ConfidenceResult> SearchImage(string query)
        {
            var fileLists = ImageAlgorithm.PartitionFiles(new DirectoryInfo(ImageAlgorithm._labelsPath).GetFiles() ,Environment.ProcessorCount);
            List<Thread> threads = new List<Thread>();
            var heap = new SortedSet<(ConfidenceResult Item, int UniqueId)>(new HeapComparer());
            object _lock = new object();
            int globalId = 0;

            for (int i = 0; i < fileLists.Count; i++)
            {
                int threadIndex = i;
                threads.Add(new Thread(() =>
                {
                    ThreadAffinityHelper.SetAffinity(threadIndex);
                    var files = fileLists[threadIndex];
                    List<ConfidenceResult> results = new List<ConfidenceResult>();
                    for (int j = 0; j < files.Count; j++)
                    {
                        var file = files[j];
                        string labelPath = file.FullName;
                        var (confidence , displayName) = LabelDecoder.ToConfidence(Json.Read(labelPath), query);
                        string imagePath = $"{ImageAlgorithm._imagesPath}{Path.GetFileNameWithoutExtension(labelPath)}.jpg";
                        var result = new ConfidenceResult
                        {
                            Score = confidence,
                            Path = imagePath,
                            DisplayName = displayName
                        };
                        if (results.Count < 25)
                        {
                            results.Add(result);
                            if (results.Count == 25)
                                results.Sort((r1, r2) => r2.Score.CompareTo(r1.Score)); //desc
                        }
                        else
                        {
                            if (result.Score > results[^1].Score)
                            {
                                int insertIndex = results.Count - 1;
                                while (insertIndex > 0 && result.Score > results[insertIndex - 1].Score)
                                {
                                    results[insertIndex] = results[insertIndex - 1]; // shift right
                                    insertIndex--;
                                }
                                results[insertIndex] = result;
                            }
                        }
                    }
                    if (results.Count < 25)
                        results.Sort((r1, r2) => r2.Score.CompareTo(r1.Score)); //desc
                                                                                //filling heap

                    lock (_lock)
                    {
                        for (int j = 0; j < results.Count; j++)
                        {
                            var result = results[j];
                            if (heap.Count < 25)
                            {
                                int id = Interlocked.Increment(ref globalId);
                                heap.Add((result, id++));
                            }
                            else
                            {
                                var min = heap.Min;
                                if (results[j].Score > min.Item.Score)
                                {
                                    heap.Remove(min);
                                    int id = Interlocked.Increment(ref globalId);
                                    heap.Add((result, id));
                                }
                                else
                                {
                                    break;
                                }
                            }
                        }

                    }
                }));
                threads.Last().Start();
            }
            foreach (var thread in threads)
            {
                thread.Join();
            }
            return heap
                .Select(tuple => tuple.Item)
                .Reverse()
                .ToList();
        }




        private static List<List<FileInfo>> PartitionFiles(FileInfo[] files, int numPartitions)
        {
            var partitions = new List<List<FileInfo>>(numPartitions);
            for (int i = 0; i < numPartitions; i++)
                partitions.Add(new List<FileInfo>());

            int totalFiles = files.Length;
            int baseSize = totalFiles / numPartitions;
            int extra = totalFiles % numPartitions;

            int index = 0;
            for (int i = 0; i < numPartitions; i++)
            {
                int size = baseSize + (i < extra ? 1 : 0);
                for (int j = 0; j < size; j++)
                {
                    if (index < totalFiles)
                        partitions[i].Add(files[index++]);
                }
            }

            return partitions;
        }


        public class SearchImageAutocomplete : AutocompleteHandler
        {
            public override async Task<AutocompletionResult> GenerateSuggestionsAsync(IInteractionContext context, IAutocompleteInteraction autocompleteInteraction, IParameterInfo parameter, IServiceProvider services)
            {
                string current = autocompleteInteraction.Data.Current.Value.ToString();
    
                var results = ImageAlgorithm.SearchImage(current);

                var options = results.
                    Select(result=> new AutocompleteResult(result.DisplayName,result.Path));
                
                return AutocompletionResult.FromSuccess(options);
            }
        }
    }
}
