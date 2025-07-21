using Discord;
using Discord.Audio;
using Discord.WebSocket;
using Microsoft.VisualBasic;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using YoutubeExplode.Playlists;

namespace DiscordBot
{
    public class PlaylistSystem
    {
        public static readonly int UPDATE_INTERVAL_SECOND = 6;
        private static ConcurrentDictionary<ulong, Playlist> _playlists = new ConcurrentDictionary<ulong, Playlist>();

        public static Playlist GetorCreatePlaylist(SocketGuild guild , IAudioClient vc)
        {
            return _playlists.GetOrAdd(guild.Id,_=> new Playlist(guild,vc));
        }

        public static Playlist? GetPlaylist(ulong id)
        {
            if (_playlists.ContainsKey(id)) return _playlists[id];
            return null;
        }

        public static void RemovePlaylist(ulong guildId)
        {
            _playlists.TryRemove(guildId, out _);
        }

        public static async Task LoopCheckVoiceChannelAndUsers()
        {
            await Task.Run(async () =>
            {
                Timer loopCheckTimer = new Timer(async _ =>
                {
                    try
                    {
                        ulong[] ids = PlaylistSystem._playlists.Keys.ToArray();

                        foreach (ulong id in ids)
                        {
                            Playlist playlist = PlaylistSystem._playlists[id];
                            if (playlist._vc == null || playlist._vc.ConnectionState != ConnectionState.Connected)
                            {
                                await playlist.Finish();
                            }
                            else
                            {
                                if (playlist._svc != null && playlist._svc.ConnectedUsers.Where(user => !user.IsBot).Count() < 1)
                                {
                                    await playlist._message.Channel.SendMessageAsync($"{GlobalVariable.botNickname}偵測到語音裡沒有人，我要退出苦來西苦！");
                                    await playlist.Finish();
                                    await Utils.DisconnectFromSVC(playlist._svc);
                                }
                                
                            }
                        }
                    }
                    catch (Exception e)
                    {
                        Console.WriteLine($"[Erro][LoopCheck] {e}");
                    }

                }, null, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5));

                GlobalVariable.PermanentTimers.Add(loopCheckTimer);
            });

        }
    }

    public class Playlist
    {
        internal readonly SocketGuild _guild;
        internal readonly IAudioClient _vc;
        internal readonly List<Tuple<WebOption, string>> _urls = new List<Tuple<WebOption, string>>();
        internal int _playingIndex = 0;
        internal bool _isPaused = false;
        internal bool _repeat = false;
        internal bool _interrupt = false;

        internal SocketVoiceChannel _svc;
        internal SocketInteraction _interaction;
        internal BufferedAudioStream _currentBufferedAudio;
        internal IUserMessage _message;
        internal Timer? _updateTimer;
        internal DateTime _startTime;
        internal DateTime _pauseTime;
        internal MediaProcess.AudioInfo? _currentTrack;
        internal AudioOutStream? _discordStream;
        internal Process? _ffmpeg;
        internal CancellationTokenSource _cts = new CancellationTokenSource();

        public Playlist(SocketGuild guild , IAudioClient vc)
        {
            this._guild = guild;
            this._vc = vc;
            var guildBot = this._guild.GetUser(GlobalVariable.botID);
            this._svc = guildBot.VoiceChannel;
        }

        public void AddUrls(List<Tuple<WebOption, string>> urls)
        {
            this._urls.AddRange(urls);
        }
        public async Task StartAsync(SocketInteraction interaction)
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
            ,null,TimeSpan.FromSeconds(PlaylistSystem.UPDATE_INTERVAL_SECOND),TimeSpan.FromSeconds(PlaylistSystem.UPDATE_INTERVAL_SECOND));
            
        }

        private void CreateDiscordStream()
        {
            this._discordStream = this._vc.CreatePCMStream(AudioApplication.Music, bufferMillis: 1500);
        }

        private async Task PlayTrackAsync(SocketInteraction interaction)
        {
            if (this._discordStream == null)
            {
                this.CreateDiscordStream();
                await this.PlayTrackAsync(interaction);
                return;
            }

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

                    while ((bytesRead = await this._currentBufferedAudio.ReadAsync(buffer,0,buffer.Length,_cts.Token)) > 0 || this._isPaused)
                    {
                        if(!this._isPaused) await this._discordStream.WriteAsync(buffer.AsMemory(0,bytesRead),this._cts.Token);
                    }
                    this._currentBufferedAudio.Dispose();
                    await this._discordStream.FlushAsync();
                }
                finally
                {
                    this._cts = new CancellationTokenSource();
                    await this.MoveToNextTrack();
                }
            });

            if (this._message == null)
            {
                this._message = await interaction.Channel.SendMessageAsync(embed:this.BuildTrackEmbed(),components:ButtonHelper.CreateView(this),flags:MessageFlags.SuppressNotification);
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
                        msg.Embed = this.BuildTrackEmbed();
                        msg.Components = ButtonHelper.CreateView(this);
                        msg.Flags = MessageFlags.SuppressNotification;
                    });
                }
                catch(Exception e)
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

        private Embed BuildTrackEmbed()
        {
            TimeSpan playedTime = (_isPaused) ? _pauseTime - _startTime : DateTime.Now - _startTime;
            string desc = $"{GlobalVariable.botNickname}正在唱第{this._playingIndex + 1}首歌，後面還有{this._urls.Count - this._playingIndex - 1}首要唱！" + Environment.NewLine +
                            this.BuildProgressBar(playedTime, TimeSpan.FromSeconds(this._currentTrack?.Duration ?? 0));
            string repeatString = (this._repeat) ? "[循環播放]" : "[正常播放]";
            var builder = new EmbedBuilder()
                .WithTitle($"[{this._urls[this._playingIndex].Item1}] {this._currentTrack?.Title ?? "未知"} - {this._currentTrack?.Creator ?? "未知"} {repeatString}")
                .WithDescription(desc)
                .WithColor(Color.DarkBlue);
            return builder.Build();
        }

        public async Task Finish()
        {
            this._interrupt = true;
            this._updateTimer?.Dispose();

            if (this._discordStream != null)
                await this._discordStream.DisposeAsync();

            if (this._message != null)
                await this._message.ModifyAsync(msg => { msg.Content = "已結束/終止"; msg.Components = new ComponentBuilder().Build(); });

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
            this._repeat=!this._repeat; 
        }

        public string GetCurrentUrl() => this._urls[this._playingIndex].Item2 ?? "https://www.youtube.com/watch?v=dQw4w9WgXcQ";
    }

    public static class ButtonHelper
    {
        public static MessageComponent CreateView(Playlist playlist)
        {
            ulong id = playlist._guild.Id;
            var builder = new ComponentBuilder()
                .WithButton("▶️ 暫停/繼續", $"pause_resume_{id}" , ButtonStyle.Success )
                .WithButton("⏭️ 下一首", $"skip_{id}", ButtonStyle.Primary)
                .WithButton("⏹️ 終止播放", $"stop_{id}", ButtonStyle.Danger)
                .WithButton("🔁 循環播放", $"loop_{id}", ButtonStyle.Secondary)
                .WithButton("🔗 連結", null, ButtonStyle.Link, url: playlist.GetCurrentUrl());

            return builder.Build();
        }

        public static async Task OnComponentExecuted(SocketMessageComponent component)
        {
            var id = component.Data.CustomId;

            if (id.StartsWith("pause_resume_"))
            {
                var guildId = ulong.Parse(id.Split("pause_resume_")[1]);
                var playlist = PlaylistSystem.GetPlaylist(guildId);
                if (playlist != null)
                    await playlist.onButtonPauseResume();
                await component.DeferAsync();
            }
            else if (id.StartsWith("skip_"))
            {
                var guildId = ulong.Parse(id.Split("skip_")[1]);
                var playlist = PlaylistSystem.GetPlaylist(guildId);
                if (playlist != null)
                    await playlist.onButtonSkip();
                await component.DeferAsync();
            }
            else if (id.StartsWith("stop_"))
            {
                var guildId = ulong.Parse(id.Split("stop_")[1]);
                var playlist = PlaylistSystem.GetPlaylist(guildId);
                if (playlist != null)
                    await playlist.onButtonStop();
                await component.DeferAsync();
            }
            else if (id.StartsWith("loop_"))
            {
                var guildId = ulong.Parse(id.Split("loop_")[1]);
                var playlist = PlaylistSystem.GetPlaylist(guildId);
                if (playlist != null)
                    await playlist.onButtonRepeat();
                await component.DeferAsync();
            }
        }

    }

    public class ConcurrentPlaylistSystem
    {
        private ConcurrentDictionary<ulong, ConcurrentDictionary<string, string>> _playlist = new ConcurrentDictionary<ulong, ConcurrentDictionary<string, string>>();
        private object _lock = new object();
        private string _path;
        private bool _isChanged = false;

        public ConcurrentPlaylistSystem(string jsonFilePath , bool enableLoopCheck)
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
                Json.Write(jobj,this._path);
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
        public Dictionary<string,string> GetPlaylists(ulong serverID)
        {
            if (!this.Exist(serverID))
                return new Dictionary<string, string>();
            return this._playlist[serverID]
                .ToDictionary();
        }


        public bool Exist(ulong serverID , string name)
        {
            if (!this._playlist.ContainsKey(serverID)) return false;
            return this._playlist[serverID].ContainsKey(name);
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
                },null,10*1000,10*1000);
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
