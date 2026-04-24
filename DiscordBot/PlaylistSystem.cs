using NetCord;
using NetCord.Gateway;
using NetCord.Gateway.Voice;
using NetCord.Rest;
using Newtonsoft.Json.Linq;
using System.Collections.Concurrent;
using System.Diagnostics;

namespace DiscordBot
{
    public class PlaylistSystem
    {
        public const int UPDATE_INTERVAL_SECOND = 6;
        private static ConcurrentDictionary<ulong, Playlist> _playlists = new ConcurrentDictionary<ulong, Playlist>();

        public static Playlist GetorCreatePlaylist(Guild guild, VoiceClient vc , ulong channelId)
        {
            return _playlists.GetOrAdd(guild.Id, _ => new Playlist(guild, vc , channelId));
        }

        public static Playlist? GetPlaylist(ulong id)
        {
            return _playlists.TryGetValue(id,out var playlist)? playlist : null;
        }

        public static void RemovePlaylist(ulong guildId)
        {
            _playlists.TryRemove(guildId, out _);
        }
    }

    public class Playlist
    {
        internal readonly Guild _guild;
        internal readonly ulong channelId;
        internal readonly VoiceClient _vc;
        internal readonly List<Tuple<WebOption, string>> _urls = new List<Tuple<WebOption, string>>();
        internal int _playingIndex = 0;
        internal bool _isPaused = false;
        internal bool _repeat = false;
        internal bool _interrupt = false;

        internal Interaction _interaction;
        internal BufferedAudioStream _currentBufferedAudio;
        internal RestMessage _message;
        internal Timer? _updateTimer;
        internal DateTime _startTime;
        internal DateTime _pauseTime;
        internal MediaProcess.AudioInfo? _currentTrack;
        internal Stream? _discordStream;
        internal Process? _ffmpeg;
        internal CancellationTokenSource _cts = new CancellationTokenSource();

        public Playlist(Guild guild, VoiceClient vc , ulong channelId)
        {
            this._guild = guild;
            this._vc = vc;
            this.channelId = channelId;
        }

        public void AddUrls(List<Tuple<WebOption, string>> urls)
        {
            this._urls.AddRange(urls);
        }
        public async Task StartAsync(Interaction interaction)
        {
            if (_urls.Count == 0) return;
            this._playingIndex = 0;
            this._interrupt = false;
            this._interaction = interaction;
            this.CreateDiscordStream();


            await this.PlayTrackAsync(interaction);

            this._updateTimer = new Timer(async _ =>
            {
                await this.UpdateMessage();
            }
            , null, TimeSpan.FromSeconds(PlaylistSystem.UPDATE_INTERVAL_SECOND), TimeSpan.FromSeconds(PlaylistSystem.UPDATE_INTERVAL_SECOND));
        }

        private void CreateDiscordStream()
        {
            this._discordStream = this._vc.CreateOutputStream();
        }

        private async Task PlayTrackAsync(Interaction interaction)
        {
            var url = this._urls[this._playingIndex];
            var urlAlgorithm = MediaProcess.DetermineAudioUrlAlgorithm(url.Item1);

            var audioInfo = await urlAlgorithm(url.Item2);
            this._currentTrack = audioInfo;

            if (audioInfo == null)
            {
                await this.MoveToNextTrack();
                return;
            }

            var ffmpeg = await MediaProcess.CreateStreamAsync(audioInfo);

            if (ffmpeg == null)
            {
                await this.MoveToNextTrack();
                return;
            }

            this._ffmpeg = ffmpeg;
            this._currentBufferedAudio = new BufferedAudioStream();

            _ = Task.Run(() => this._currentBufferedAudio.FeedFromStreamAsync(ffmpeg.StandardOutput.BaseStream));

            this._startTime = DateTime.Now;

            _ = Task.Run(async () =>
            {
                try
                {
                    byte[] buffer = new byte[3840];
                    int bytesRead;

                    OpusEncodeStream stream = new(this._discordStream, PcmFormat.Short, VoiceChannels.Stereo, OpusApplication.Audio);

                    while (true)
                    {
                        if (this._interrupt)
                            break;

                        if (!this._isPaused)
                        {
                            bytesRead = await this._currentBufferedAudio.ReadAsync(buffer, 0, buffer.Length, _cts.Token);

                            if (bytesRead <= 0)
                                break;

                            if (bytesRead < buffer.Length)
                            {
                                Array.Clear(buffer, bytesRead, buffer.Length - bytesRead);
                            }

                            await stream.WriteAsync(buffer, this._cts.Token);
                        }
                    }
                    this._currentBufferedAudio.Dispose();
                    await stream.FlushAsync();
                }
                finally
                {
                    this._cts = new CancellationTokenSource();
                    await this.MoveToNextTrack();
                }
            });

            if (this._message == null)
            {
                var message = new InteractionMessageProperties()
                    .AddEmbeds(this.BuildTrackEmbed())
                    .AddComponents(ButtonHelper.CreateView(this));
                this._message = await interaction.SendFollowupMessageAsync(message);
            }

        }

        private async Task MoveToNextTrack()
        {
            if (!this._repeat)
            {
                this._playingIndex++;
                if (this._playingIndex >= this._urls.Count)
                {
                    await this.Finish();
                    return;
                }
            }
            await this.PlayTrackAsync(this._interaction);
        }

        private async Task UpdateMessage()
        {
            if (_message != null)
            {
                try
                {
                    await _message.ModifyAsync(msg =>
                    {
                        msg.Embeds = new[]{this.BuildTrackEmbed()};
                    });
                }
                catch (Exception e)
                {
                    Console.WriteLine($"[Error] Encounter error when updating message !{Environment.NewLine}{e}");
                }
            }
        }

        private string BuildProgressBar(TimeSpan current, TimeSpan total, int barLength = 40)
        {
            if (total.TotalSeconds <= 0)
                return "[?]";

            if (current > total && current != TimeSpan.Zero && total != TimeSpan.Zero)
                current = total;

            double progress = Math.Clamp(current.TotalSeconds / total.TotalSeconds, 0, 1);
            int filledLength = (int)Math.Min(barLength - 1, barLength * progress);
            string bar = new string('=', filledLength) + '>' + new string('-', barLength - filledLength - 1);
            return $"[{bar}] {current:mm\\:ss} / {total:mm\\:ss} [{(this._isPaused ? "暫停中" : "播放中")}]";
        }

        private EmbedProperties BuildTrackEmbed()
        {
            TimeSpan playedTime = (_isPaused) ? _pauseTime - _startTime : DateTime.Now - _startTime;
            string desc = $"{GlobalVariable.botNickname}正在唱第{this._playingIndex + 1}首歌，後面還有{this._urls.Count - this._playingIndex - 1}首要唱！" + Environment.NewLine +
                            this.BuildProgressBar(playedTime, TimeSpan.FromSeconds(this._currentTrack?.Duration ?? 0));
            string repeatString = (this._repeat) ? "[循環播放]" : "[正常播放]";
            var builder = new EmbedProperties()
                .WithTitle($"[{this._urls[this._playingIndex].Item1}] {this._currentTrack?.Title ?? "未知"} - {this._currentTrack?.Creator ?? "未知"} {repeatString}")
                .WithDescription(desc)
                .WithColor(new Color(0, 0, 139));
            return builder;
        }

        public async Task Finish()
        {
            this._interrupt = true;
            this._updateTimer?.Dispose();

            if (this._discordStream != null)
                await this._discordStream.DisposeAsync();

            if (this._message != null)
                await this._message.ModifyAsync(msg => 
                { 
                    msg.Content = "已結束/終止";
                    msg.Embeds = null;
                    msg.Components = null;
                });

            PlaylistSystem.RemovePlaylist(this._guild.Id);
        }

        public async Task onButtonPauseResume()
        {
            if (this._isPaused)
            {
                this._currentBufferedAudio?.Resume();
                this._startTime += DateTime.Now - this._pauseTime;
            }
            else
            {
                this._currentBufferedAudio?.Pause();
                this._pauseTime = DateTime.Now;
            }
            this._isPaused = !this._isPaused;
        }
        public async Task onButtonSkip()
        {
            if (this._currentBufferedAudio != null)
                this._cts.Cancel();
        }
        public async Task onButtonStop()
        {
            await this.Finish();
        }

        public async Task onButtonRepeat()
        {
            this._repeat = !this._repeat;
        }

        public string GetCurrentUrl() => this._urls[this._playingIndex].Item2 ?? "https://www.youtube.com/watch?v=dQw4w9WgXcQ";
    }

    public static class ButtonHelper
    {
        public static ActionRowProperties CreateView(Playlist playlist)
        {
            ulong id = playlist._guild.Id;

            var component = new ActionRowProperties()
                .AddComponents
                (
                    new ButtonProperties($"pause_resume_{id}", "▶️ 暫停/繼續", ButtonStyle.Success),
                    new ButtonProperties($"skip_{id}", "⏭️ 下一首", ButtonStyle.Primary),
                    new ButtonProperties($"stop_{id}", "⏹️ 終止播放" , ButtonStyle.Danger),
                    new ButtonProperties($"loop_{id}", "🔁 循環播放" , ButtonStyle.Secondary),
                    new LinkButtonProperties(playlist.GetCurrentUrl(), "🔗 連結")
                );

            return component;
        }

        public static async Task OnComponentExecuted(ButtonInteraction interaction)
        {
            var id = interaction.Data.CustomId;
            var callback = InteractionCallback.DeferredModifyMessage;

            if (id.StartsWith("pause_resume_"))
            {
                var guildId = ulong.Parse(id.Split("pause_resume_")[1]);
                var playlist = PlaylistSystem.GetPlaylist(guildId);
                if (playlist != null)
                    await playlist.onButtonPauseResume();
                await interaction.SendResponseAsync(callback);
            }
            else if (id.StartsWith("skip_"))
            {
                var guildId = ulong.Parse(id.Split("skip_")[1]);
                var playlist = PlaylistSystem.GetPlaylist(guildId);
                if (playlist != null)
                    await playlist.onButtonSkip();
                await interaction.SendResponseAsync(callback);
            }
            else if (id.StartsWith("stop_"))
            {
                var guildId = ulong.Parse(id.Split("stop_")[1]);
                var playlist = PlaylistSystem.GetPlaylist(guildId);
                if (playlist != null)
                    await playlist.onButtonStop();
                await interaction.SendResponseAsync(callback);
            }
            else if (id.StartsWith("loop_"))
            {
                var guildId = ulong.Parse(id.Split("loop_")[1]);
                var playlist = PlaylistSystem.GetPlaylist(guildId);
                if (playlist != null)
                    await playlist.onButtonRepeat();
                await interaction.SendResponseAsync(callback);
            }
        }
        
    }

    public class ConcurrentPlaylistSystem
    {
        private ConcurrentDictionary<ulong, ConcurrentDictionary<string, string>> _playlist = new ConcurrentDictionary<ulong, ConcurrentDictionary<string, string>>();
        private object _lock = new object();
        private string _path;
        private bool _isChanged = false;

        public ConcurrentPlaylistSystem(string jsonFilePath, bool enableLoopCheck)
        {
            this._path = jsonFilePath;
            this.Read();
            if (enableLoopCheck)
                this.LoopCheckAndWrite();
        }
        public void Read()
        {
            lock (this._lock)
            {
                var jobj = Json.Read(this._path);
                this._playlist = ConcurrentPlaylistSystem.JObjectToConcurrentDict(jobj);
            }
        }
        public void Write()
        {
            lock (this._lock)
            {
                var jobj = ConcurrentPlaylistSystem.ConcurrentDictToJObject(this._playlist);
                Json.Write(jobj, this._path);
            }
        }
        /// <summary>
        /// Add only , cannot update . To update ,delete and add
        /// </summary>
        public void AddOrCreate(ulong serverID, string name, string url)
        {
            var dict = _playlist.GetOrAdd(serverID, _ => new ConcurrentDictionary<string, string>());
            if (dict.TryAdd(name, url))
                this._isChanged = true;
        }

        public void Remove(ulong serverID, string name)
        {
            if (this._playlist.TryGetValue(serverID, out var dict))
            {
                if (dict.TryRemove(name, out _))
                    this._isChanged = true;
            }
        }
        public Dictionary<string, string> GetPlaylists(ulong serverID)
        {
            if (!this.Exist(serverID))
                return new Dictionary<string, string>();
            return this._playlist[serverID]
                .ToDictionary();
        }

        public bool Exist(ulong serverID)
        {
            return this._playlist.ContainsKey(serverID);
        }

        private void LoopCheckAndWrite()
        {
            Task.Run(() =>
            {
                Timer t = new Timer((e) =>
                {
                    if (this._isChanged)
                    {
                        this._isChanged = false;
                        this.Write();
                    }
                }, null, 10 * 1000, 10 * 1000);
                GlobalVariable.PermanentTimers.Add(t);
            });

        }


        private static JObject ConcurrentDictToJObject(ConcurrentDictionary<ulong, ConcurrentDictionary<string, string>> source)
        {
            var result = new JObject();

            foreach (var (guildId, playlistDict) in source)
            {
                var guildObject = new JObject();

                foreach (var (playlistName, url) in playlistDict)
                {
                    guildObject[playlistName] = url;
                }

                result[guildId.ToString()] = guildObject;
            }

            return result;
        }

        private static ConcurrentDictionary<ulong, ConcurrentDictionary<string, string>> JObjectToConcurrentDict(JObject jObject)
        {
            var result = new ConcurrentDictionary<ulong, ConcurrentDictionary<string, string>>();

            foreach (var guildProperty in jObject.Properties())
            {
                if (ulong.TryParse(guildProperty.Name, out ulong guildId))
                {
                    var playlists = new ConcurrentDictionary<string, string>();

                    if (guildProperty.Value is JObject playlistObj)
                    {
                        foreach (var playlistProp in playlistObj.Properties())
                        {
                            playlists[playlistProp.Name] = playlistProp.Value?.ToString() ?? string.Empty;
                        }

                        result[guildId] = playlists;
                    }
                }
            }

            return result;
        }

    }

}
