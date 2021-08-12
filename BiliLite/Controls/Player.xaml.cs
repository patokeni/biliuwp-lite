﻿using BiliLite.Helpers;
using BiliLite.Modules;
using FFmpegInterop;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text;
using System.Threading.Tasks;
using Windows.Foundation;
using Windows.Foundation.Collections;
using Windows.Media;
using Windows.Media.Core;
using Windows.Media.Editing;
using Windows.Media.Playback;
using Windows.Media.Streaming.Adaptive;
using Windows.Storage;
using Windows.Storage.Streams;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Controls.Primitives;
using Windows.UI.Xaml.Data;
using Windows.UI.Xaml.Input;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Navigation;
using Windows.Web.Http;

//https://go.microsoft.com/fwlink/?LinkId=234236 上介绍了“用户控件”项模板

namespace BiliLite.Controls
{
    public enum PlayState
    {
        Loading,
        Playing,
        Pause,
        End,
        Error
    }
    public enum PlayEngine
    {
        Native = 1,
        FFmpegInteropMSS = 2,
        SYEngine = 3,
        FFmpegInteropMSSH265 = 4,
        VLC = 5
    }
    public enum PlayMediaType
    {
        Single,
        MultiFlv,
        Dash
    }
    //TODO 写得太复杂了，需要重写
    public sealed partial class Player : UserControl, IDisposable, INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler PropertyChanged;
        private void DoPropertyChanged(string name)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
        public PlayState PlayState { get; set; }
        public PlayMediaType PlayMediaType { get; set; }
        public VideoPlayHistoryHelper.ABPlayHistoryEntry ABPlay { get; set; }
        private DashItemModel _dash_video;
        private DashItemModel _dash_audio;
        private PlayEngine current_engine;

        private FFmpegInteropMSS _ffmpegMSSVideo;
        private MediaPlayer _playerVideo;
        //音视频分离
        private FFmpegInteropMSS _ffmpegMSSAudio;
        private MediaPlayer _playerAudio;
        private MediaTimelineController _mediaTimelineController;

        //多段FLV
        private List<FFmpegInteropMSS> _ffmpegMSSItems;
        private MediaPlaybackList _mediaPlaybackList;




        /// <summary>
        /// 播放状态变更
        /// </summary>
        public event EventHandler<PlayState> PlayStateChanged;
        /// <summary>
        /// 媒体加载完成
        /// </summary>
        public event EventHandler PlayMediaOpened;
        /// <summary>
        /// 播放完成
        /// </summary>
        public event EventHandler PlayMediaEnded;

        /// <summary>
        /// 播放错误
        /// </summary>
        public event EventHandler<string> PlayMediaError;
        /// <summary>
        /// 更改播放引擎
        /// </summary>
        public event EventHandler<ChangePlayerEngine> ChangeEngine;

        /// <summary>
        /// 进度
        /// </summary>
        public double Position
        {
            get { return (double)GetValue(PositionProperty); }
            set { SetValue(PositionProperty, value); }
        }
        public static readonly DependencyProperty PositionProperty =
            DependencyProperty.Register("Position", typeof(double), typeof(Player), new PropertyMetadata(0.0, OnPositionChanged));
        private static void OnPositionChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var sender = d as Player;
            if (sender.ABPlay != null && sender.ABPlay.PointB != 0 && (double) e.NewValue > sender.ABPlay.PointB)
            {
                sender.Position = sender.ABPlay.PointA;
                return;
            }
            if (Math.Abs((double)e.NewValue - (double)e.OldValue) > 1)
            {
                if (sender.PlayState == PlayState.Playing || sender.PlayState == PlayState.Pause)
                {
                    sender.SetPosition((double)e.NewValue);
                }
            }
        }

        /// <summary>
        /// 时长
        /// </summary>
        public double Duration
        {
            get { return (double)GetValue(DurationProperty); }
            set { SetValue(DurationProperty, value); }
        }

        public static readonly DependencyProperty DurationProperty =
            DependencyProperty.Register("Duration", typeof(double), typeof(Player), new PropertyMetadata(0.0));

        /// <summary>
        /// 音量0-1
        /// </summary>
        public double Volume
        {
            get { return (double)GetValue(VolumeProperty); }
            set
            {
                if (value > 1)
                {
                    value = 1;
                }
                if (value < 0)
                {
                    value = 0;
                }
                SetValue(VolumeProperty, value);
            }
        }
        public static readonly DependencyProperty VolumeProperty =
            DependencyProperty.Register("Volume", typeof(double), typeof(Player), new PropertyMetadata(1.0, OnVolumeChanged));
        private static void OnVolumeChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var sender = d as Player;
            var value = (double)e.NewValue;
            if (value > 1)
            {
                value = 1;
            }
            else if (value < 0)
            {
                value = 0;
            }
            sender.SetVolume(value);
        }


        /// <summary>
        /// 是否缓冲中
        /// </summary>
        public bool Buffering
        {
            get { return (bool)GetValue(BufferingProperty); }
            set { SetValue(BufferingProperty, value); }
        }
        public static readonly DependencyProperty BufferingProperty =
            DependencyProperty.Register("Buffering", typeof(bool), typeof(Player), new PropertyMetadata(false));


        /// <summary>
        /// 缓冲进度,0-100
        /// </summary>
        public double BufferCache
        {
            get { return (double)GetValue(BufferCacheProperty); }
            set { SetValue(BufferCacheProperty, value); }
        }


        public static readonly DependencyProperty BufferCacheProperty =
            DependencyProperty.Register("BufferCache", typeof(double), typeof(Player), new PropertyMetadata(1));




        /// <summary>
        /// 播放速度
        /// </summary>
        public double Rate { get; set; } = 1.0;


        /// <summary>
        /// 媒体信息
        /// </summary>
        public string MediaInfo
        {
            get { return (string)GetValue(MediaInfoProperty); }
            set { SetValue(MediaInfoProperty, value); }
        }

        // Using a DependencyProperty as the backing store for MediaInfo.  This enables animation, styling, binding, etc...
        public static readonly DependencyProperty MediaInfoProperty =
            DependencyProperty.Register("MediaInfo", typeof(string), typeof(Player), new PropertyMetadata(""));


        public bool Opening { get; set; }

        public SystemMediaTransportControls SystemMediaTransportControls
        {
            get {
                if (_playerAudio != null)
                {
                    if (_playerAudio.CommandManager.IsEnabled)
                    {
                        _playerAudio.CommandManager.IsEnabled = false;
                    }
                    return _playerAudio.SystemMediaTransportControls;
                } else if (_playerVideo != null)
                {
                    if (_playerVideo.CommandManager.IsEnabled)
                    {
                        _playerVideo.CommandManager.IsEnabled = false;
                    }
                    return _playerVideo.SystemMediaTransportControls;
                }

                return null;
            }
        }

        public Player()
        {
            this.InitializeComponent();

            // We don't have ARM64 support of SYEngine.
            if (RuntimeInformation.ProcessArchitecture != Architecture.Arm64)
            {
                SYEngine.Core.ForceNetworkMode = true;
                SYEngine.Core.ForceSoftwareDecode = !SettingHelper.GetValue<bool>(SettingHelper.Player.HARDWARE_DECODING, false);
            }
            //_ffmpegConfig.StreamBufferSize = 655360;//1024 * 30;

        }
        /// <summary>
        /// 使用AdaptiveMediaSource播放视频
        /// </summary>
        /// <param name="videoUrl"></param>
        /// <param name="audioUrl"></param>
        /// <param name="positon"></param>
        /// <param name="needConfig"></param>
        /// <returns></returns>
        public async Task<PlayerOpenResult> PlayerDashUseNative(DashItemModel videoUrl, DashItemModel audioUrl, IDictionary<string, string> header = null, double positon = 0, bool needConfig = true)
        {
            try
            {
                mediaPlayerVideo.Visibility = Visibility.Visible;
                //vlcVideoView.Visibility = Visibility.Collapsed;
                _dash_video = videoUrl;
                _dash_audio = audioUrl;
                Opening = true;
                current_engine = PlayEngine.Native;
                PlayMediaType = PlayMediaType.Dash;
                //加载中
                PlayState = PlayState.Loading;
                PlayStateChanged?.Invoke(this, PlayState);
                ClosePlay();


                //设置播放器
                _playerVideo = new MediaPlayer();
                //_playerVideo.Source = MediaSource.CreateFromUri(new Uri(videoUrl.baseUrl));
                var mediaSource = await CreateAdaptiveMediaSource(videoUrl, audioUrl, header);
                if (mediaSource == null)
                {
                    return new PlayerOpenResult()
                    {
                        result = false,
                        message = "创建MediaSource失败"
                    };
                }

                _playerVideo.Source = MediaSource.CreateFromAdaptiveMediaSource(mediaSource);
                //设置时长
                _playerVideo.MediaOpened += new TypedEventHandler<MediaPlayer, object>(async (e, arg) =>
                {
                    Opening = false;
                    await this.Dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Normal, () =>
                    {
                        Duration = _playerVideo.PlaybackSession.NaturalDuration.TotalSeconds;
                        PlayMediaOpened?.Invoke(this, new EventArgs());

                        ////设置进度
                        //if (positon != 0)
                        //{
                        //    _playerVideo.PlaybackSession.Position = TimeSpan.FromSeconds(positon);
                        //}
                    });
                });

                //播放完成
                _playerVideo.MediaEnded += new TypedEventHandler<MediaPlayer, object>(async (e, arg) =>
                {
                    await this.Dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Normal, () =>
                    {
                        //加个判断，是否真的播放完成了
                        if (Position.ToInt32() >= Duration.ToInt32())
                        {
                            PlayState = PlayState.End;
                            Position = 0;
                            PlayStateChanged?.Invoke(this, PlayState);
                            PlayMediaEnded?.Invoke(this, new EventArgs());
                        }
                    });
                });
                //播放错误
                _playerVideo.MediaFailed += new TypedEventHandler<MediaPlayer, MediaPlayerFailedEventArgs>(async (e, arg) =>
                {
                    await this.Dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Normal, () =>
                    {
                        PlayState = PlayState.Error;
                        PlayStateChanged?.Invoke(this, PlayState);
                        ChangeEngine?.Invoke(this, new ChangePlayerEngine()
                        {
                            change_engine = PlayEngine.FFmpegInteropMSS,
                            current_mode = PlayEngine.Native,
                            need_change = true,
                            play_type = PlayMediaType.Dash
                        });
                    });
                });
                //缓冲开始
                _playerVideo.PlaybackSession.BufferingStarted += new TypedEventHandler<MediaPlaybackSession, object>(async (e, arg) =>
                {
                    await this.Dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Normal, () =>
                    {
                        Buffering = true;
                    });
                });
                //缓冲进行中
                _playerVideo.PlaybackSession.BufferingProgressChanged += new TypedEventHandler<MediaPlaybackSession, object>(async (e, arg) =>
                {
                    await this.Dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Normal, () =>
                    {
                        Buffering = true;
                        BufferCache = e.BufferingProgress;
                    });


                });
                //缓冲进行中
                _playerVideo.PlaybackSession.BufferingEnded += new TypedEventHandler<MediaPlaybackSession, object>(async (e, arg) =>
                {
                    await this.Dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Normal, () =>
                    {
                        Buffering = false;
                    });
                });
                //进度变更
                _playerVideo.PlaybackSession.PositionChanged += new TypedEventHandler<MediaPlaybackSession, object>(async (e, arg) =>
                {
                    await this.Dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Normal, () =>
                    {
                        try
                        {
                            Position = e.Position.TotalSeconds;
                        }
                        catch (Exception)
                        {
                        }
                    });
                });

                PlayState = PlayState.Pause;
                PlayStateChanged?.Invoke(this, PlayState);
                //设置音量
                _playerVideo.Volume = Volume;
                //设置速率
                _playerVideo.PlaybackSession.PlaybackRate = Rate;

                //绑定MediaPlayer
                mediaPlayerVideo.SetMediaPlayer(_playerVideo);

                return new PlayerOpenResult()
                {
                    result = true
                };
            }
            catch (Exception ex)
            {
                //PlayMediaError?.Invoke(this, "视频加载时出错:" + ex.Message);
                return new PlayerOpenResult()
                {
                    result = false,
                    message = ex.Message,
                    detail_message = ex.StackTrace
                };
            }
        }
        /// <summary>
        /// 使用eMediaSource播放视频
        /// </summary>
        /// <param name="videoUrl"></param>
        /// <param name="audioUrl"></param>
        /// <param name="positon"></param>
        /// <param name="needConfig"></param>
        /// <returns></returns>
        public async Task<PlayerOpenResult> PlayerSingleMp4UseNativeAsync(string url, double positon = 0, bool needConfig = true, bool isLocal = false)
        {
            try
            {
                mediaPlayerVideo.Visibility = Visibility.Visible;
                //vlcVideoView.Visibility = Visibility.Collapsed;

                Opening = true;
                current_engine = PlayEngine.Native;
                PlayMediaType = PlayMediaType.Dash;
                //加载中
                PlayState = PlayState.Loading;
                PlayStateChanged?.Invoke(this, PlayState);
                ClosePlay();

                //设置播放器
                _playerVideo = new MediaPlayer();
                if (isLocal)
                {
                    _playerVideo.Source = MediaSource.CreateFromStorageFile(await StorageFile.GetFileFromPathAsync(url));
                }
                else
                {
                    _playerVideo.Source = MediaSource.CreateFromUri(new Uri(url));
                }


                //设置时长
                _playerVideo.MediaOpened += new TypedEventHandler<MediaPlayer, object>(async (e, arg) =>
                {
                    Opening = false;
                    await this.Dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Normal, () =>
                    {
                        Duration = _playerVideo.PlaybackSession.NaturalDuration.TotalSeconds;
                        PlayMediaOpened?.Invoke(this, new EventArgs());

                        ////设置进度
                        //if (positon != 0)
                        //{
                        //    _playerVideo.PlaybackSession.Position = TimeSpan.FromSeconds(positon);
                        //}
                    });
                });

                //播放完成
                _playerVideo.MediaEnded += new TypedEventHandler<MediaPlayer, object>(async (e, arg) =>
                {
                    await this.Dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Normal, () =>
                    {
                        //加个判断，是否真的播放完成了
                        if (Position.ToInt32() >= Duration.ToInt32())
                        {
                            PlayState = PlayState.End;
                            Position = 0;
                            PlayStateChanged?.Invoke(this, PlayState);
                            PlayMediaEnded?.Invoke(this, new EventArgs());
                        }
                    });
                });
                //播放错误
                _playerVideo.MediaFailed += new TypedEventHandler<MediaPlayer, MediaPlayerFailedEventArgs>(async (e, arg) =>
                {
                    await this.Dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Normal, () =>
                    {
                        PlayState = PlayState.Error;
                        PlayStateChanged?.Invoke(this, PlayState);
                        ChangeEngine?.Invoke(this, new ChangePlayerEngine()
                        {
                            change_engine = PlayEngine.FFmpegInteropMSS,
                            current_mode = PlayEngine.Native,
                            need_change = true,
                            play_type = PlayMediaType.Dash
                        });
                    });
                });
                //缓冲开始
                _playerVideo.PlaybackSession.BufferingStarted += new TypedEventHandler<MediaPlaybackSession, object>(async (e, arg) =>
                {
                    await this.Dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Normal, () =>
                    {
                        Buffering = true;
                    });
                });
                //缓冲进行中
                _playerVideo.PlaybackSession.BufferingProgressChanged += new TypedEventHandler<MediaPlaybackSession, object>(async (e, arg) =>
                {
                    await this.Dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Normal, () =>
                    {
                        Buffering = true;
                        BufferCache = e.BufferingProgress;
                    });


                });
                //缓冲进行中
                _playerVideo.PlaybackSession.BufferingEnded += new TypedEventHandler<MediaPlaybackSession, object>(async (e, arg) =>
                {
                    await this.Dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Normal, () =>
                    {
                        Buffering = false;
                    });
                });
                //进度变更
                _playerVideo.PlaybackSession.PositionChanged += new TypedEventHandler<MediaPlaybackSession, object>(async (e, arg) =>
                {
                    await this.Dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Normal, () =>
                    {
                        try
                        {
                            Position = e.Position.TotalSeconds;
                        }
                        catch (Exception)
                        {
                        }
                    });
                });

                PlayState = PlayState.Pause;
                PlayStateChanged?.Invoke(this, PlayState);
                //设置音量
                _playerVideo.Volume = Volume;
                //设置速率
                _playerVideo.PlaybackSession.PlaybackRate = Rate;

                //绑定MediaPlayer
                mediaPlayerVideo.SetMediaPlayer(_playerVideo);

                return new PlayerOpenResult()
                {
                    result = true
                };
            }
            catch (Exception ex)
            {
                //PlayMediaError?.Invoke(this, "视频加载时出错:" + ex.Message);
                return new PlayerOpenResult()
                {
                    result = false,
                    message = ex.Message,
                    detail_message = ex.StackTrace
                };
            }
        }
        /// <summary>
        /// 使用VLC播放视频
        /// </summary>
        /// <param name="videoUrl"></param>
        /// <param name="audioUrl"></param>
        /// <param name="positon"></param>
        /// <param name="needConfig"></param>
        /// <returns></returns>
        //public async Task<PlayerOpenResult> PlayerDashUseVLC(DashItemModel videoUrl, DashItemModel audioUrl, double positon = 0, bool needConfig = true)
        //{
        //    try
        //    {
        //        SystemMediaTransportControls systemMediaTransportControls= SystemMediaTransportControls.GetForCurrentView();
        //        systemMediaTransportControls.IsPlayEnabled = true;
        //        systemMediaTransportControls.IsPauseEnabled = true;
        //        mediaPlayerVideo.Visibility = Visibility.Collapsed;
        //        //vlcVideoView.Visibility = Visibility.Visible;
        //        _dash_video = videoUrl;
        //        _dash_audio = audioUrl;
        //        Opening = true;
        //        current_engine = PlayEngine.VLC;
        //        PlayMediaType = PlayMediaType.Dash;
        //        //加载中
        //        PlayState = PlayState.Loading;
        //        PlayStateChanged?.Invoke(this, PlayState);
        //        PlayState = PlayState.End;
        //        //进度设置为0
        //        Position = 0;
        //        Duration = 0;
        //        bool first= true;
        //        _vlcMediaPlayer.EnableHardwareDecoding = SettingHelper.GetValue( SettingHelper.Player.HARDWARE_DECODING,true);

        //        //设置播放器
        //        //vlcMediaPlayer = new MediaPlayer();
        //        //_playerVideo.Source = MediaSource.CreateFromUri(new Uri(videoUrl.baseUrl));
        //        LibVLCSharp.Shared.Media media = new LibVLCSharp.Shared.Media(_libVLC, new Uri(videoUrl.baseUrl));
        //        media.AddOption("http-referrer=https://www.bilibili.com/");
        //        media.AddOption("http-user-agent=Mozilla/5.0 (Windows NT 10.0; WOW64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/86.0.4240.198 Safari/537.36");
        //        media.AddSlave(LibVLCSharp.Shared.MediaSlaveType.Audio, 4, audioUrl.baseUrl);
        //        _vlcMediaPlayer.Media = media;
        //        systemMediaTransportControls.ButtonPressed += new TypedEventHandler<SystemMediaTransportControls, SystemMediaTransportControlsButtonPressedEventArgs>(async (sender, args) => {
        //            switch (args.Button)
        //            {
        //                case SystemMediaTransportControlsButton.Play:
        //                    await Dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Normal, () =>
        //                    {
        //                        _vlcMediaPlayer.Play();
        //                    });
        //                    break;
        //                case SystemMediaTransportControlsButton.Pause:
        //                    await Dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Normal, () =>
        //                    {
        //                        _vlcMediaPlayer.Pause();
        //                    });
        //                    break;
        //                default:
        //                    break;
        //            }
        //        });
        //        //设置时长
        //        media.DurationChanged += new EventHandler<LibVLCSharp.Shared.MediaDurationChangedEventArgs>(async (sender, e) =>
        //        {
        //            if (e.Duration <= 0) return;
        //            Opening = false;
        //            await this.Dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Normal, () =>
        //            {
        //                Duration = e.Duration / 1000;

        //                PlayMediaOpened?.Invoke(this, new EventArgs());

        //                ////设置进度
        //                //if (positon != 0)
        //                //{
        //                //    _playerVideo.PlaybackSession.Position = TimeSpan.FromSeconds(positon);
        //                //}
        //            });

        //        });
        //        //缓冲中
        //        _vlcMediaPlayer.Buffering += new EventHandler<LibVLCSharp.Shared.MediaPlayerBufferingEventArgs>(async (sender, e) =>
        //        {
        //            await this.Dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Normal, () =>
        //            {
        //                if (e.Cache < 100)
        //                {
        //                    Buffering = true;
        //                    BufferCache = e.Cache;
        //                }
        //                else
        //                {
        //                    Buffering = false;
        //                    BufferCache = e.Cache;
        //                }

        //            });
        //        });
        //        media.StateChanged += new EventHandler<LibVLCSharp.Shared.MediaStateChangedEventArgs>(async (sender, e) =>
        //        {
        //            switch (e.State)
        //            {
        //                case LibVLCSharp.Shared.VLCState.NothingSpecial:
        //                    break;
        //                case LibVLCSharp.Shared.VLCState.Opening:
        //                    await this.Dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Normal, () =>
        //                    {
        //                        PlayState = PlayState.Loading;
        //                    });
        //                    break;
        //                //case LibVLCSharp.Shared.VLCState.Playing:
        //                //    await this.Dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Normal, () =>
        //                //    {
        //                //        Buffering = false;
        //                //        PlayState = PlayState.Playing;
        //                //        PlayStateChanged?.Invoke(this, PlayState);
        //                //    });
        //                //    break;
        //                //case LibVLCSharp.Shared.VLCState.Paused:
        //                //    await this.Dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Normal, () =>
        //                //    {
        //                //        Buffering = false;
        //                //        PlayState = PlayState.Pause;
        //                //        PlayStateChanged?.Invoke(this, PlayState);
        //                //    });
        //                //    break;
        //                case LibVLCSharp.Shared.VLCState.Stopped:
        //                    await this.Dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Normal, () =>
        //                    {
        //                        PlayState = PlayState.End;
        //                    });
        //                    break;
        //                case LibVLCSharp.Shared.VLCState.Ended:
        //                    {
        //                        await this.Dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Normal, () =>
        //                        {
        //                            //加个判断，是否真的播放完成了
        //                            if (Position.ToInt32() >= Duration.ToInt32())
        //                            {
        //                                PlayState = PlayState.End;
        //                                Position = 0;
        //                                PlayStateChanged?.Invoke(this, PlayState);
        //                                PlayMediaEnded?.Invoke(this, new EventArgs());
        //                            }
        //                        });
        //                    }
        //                    break;
        //                case LibVLCSharp.Shared.VLCState.Error:
        //                    {
        //                        await this.Dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Normal, () =>
        //                        {
        //                            PlayState = PlayState.Error;
        //                            PlayStateChanged?.Invoke(this, PlayState);
        //                            ChangeEngine?.Invoke(this, new ChangePlayerEngine()
        //                            {
        //                                change_engine = PlayEngine.FFmpegInteropMSS,
        //                                current_mode = PlayEngine.VLC,
        //                                need_change = true,
        //                                play_type = PlayMediaType.Dash
        //                            });
        //                        });
        //                    }
        //                    break;
        //                default:
        //                    break;
        //            }

        //        });

        //        _vlcMediaPlayer.PositionChanged += new EventHandler<LibVLCSharp.Shared.MediaPlayerPositionChangedEventArgs>(async (sender, e) =>
        //        {
        //            await this.Dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Normal, () =>
        //            {
        //                Buffering = false;
        //                PlayState = PlayState.Playing;
        //                Debug.WriteLine("PPPPPPPPPPPPPP:" + e.Position);
        //                if(e.Position>0)
        //                Position = e.Position * Duration;
        //            });
        //        });
        //        _vlcMediaPlayer.Paused+=new EventHandler<EventArgs>(async (sender, e) =>
        //        {
        //            await this.Dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Normal, () =>
        //            {
        //                Buffering = false;
        //                PlayState = PlayState.Pause;
        //                systemMediaTransportControls.PlaybackStatus = MediaPlaybackStatus.Paused;
        //                PlayStateChanged?.Invoke(this, PlayState);
        //            });
        //        });
        //        _vlcMediaPlayer.Playing += new EventHandler<EventArgs>(async (sender, e) =>
        //        {
        //            await this.Dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Normal, () =>
        //            {
        //                if (first)
        //                {
        //                    first = false;
        //                    _vlcMediaPlayer.Pause();

        //                    return;
        //                }
        //                Buffering = false;
        //                PlayState = PlayState.Playing;
        //                if(systemMediaTransportControls.PlaybackStatus!= MediaPlaybackStatus.Playing)
        //                {
        //                    systemMediaTransportControls.PlaybackStatus = MediaPlaybackStatus.Playing;
        //                    PlayStateChanged?.Invoke(this, PlayState);
        //                }

        //            });
        //        });
        //        PlayState = PlayState.Pause;
        //        PlayStateChanged?.Invoke(this, PlayState);
        //        //设置音量
        //        _vlcMediaPlayer.Volume = Convert.ToInt32( Volume*100);
        //        //设置速率
        //        _vlcMediaPlayer.SetRate((float) Rate);

        //        //绑定MediaPlayer
        //        _vlcMediaPlayer.Play();

        //        return new PlayerOpenResult()
        //        {
        //            result = true
        //        };
        //    }
        //    catch (Exception ex)
        //    {
        //        //PlayMediaError?.Invoke(this, "视频加载时出错:" + ex.Message);
        //        return new PlayerOpenResult()
        //        {
        //            result = false,
        //            message = ex.Message,
        //            detail_message = ex.StackTrace
        //        };
        //    }
        //}
        /// <summary>
        /// 使用FFmpegInterop解码播放音视频分离视频
        /// </summary>
        /// <param name="videoUrl"></param>
        /// <param name="audioUrl"></param>
        /// <param name="positon"></param>
        /// <param name="needConfig"></param>
        /// <returns></returns>
        public async Task<PlayerOpenResult> PlayDashUseFFmpegInterop(DashItemModel videoUrl, DashItemModel audioUrl, IDictionary<string, string> header = null, double positon = 0, bool needConfig = true, bool isLocal = false)
        {
            try
            {
                mediaPlayerVideo.Visibility = Visibility.Visible;
                //vlcVideoView.Visibility = Visibility.Collapsed;
                Opening = true;
                _dash_video = videoUrl;
                _dash_audio = audioUrl;
                current_engine = PlayEngine.FFmpegInteropMSS;

                PlayMediaType = PlayMediaType.Dash;
                //加载中
                PlayState = PlayState.Loading;
                PlayStateChanged?.Invoke(this, PlayState);
                //关闭正在播放的视频
                ClosePlay();
                var _ffmpegConfig = CreateFFmpegInteropConfig(header);
                if (isLocal)
                {
                   
                    var videoFile = await StorageFile.GetFileFromPathAsync(videoUrl.base_url);
                    _ffmpegMSSVideo = await FFmpegInteropMSS.CreateFromStreamAsync(await videoFile.OpenAsync(FileAccessMode.Read), _ffmpegConfig);
                    if (audioUrl != null)
                    {
                        var audioFile = await StorageFile.GetFileFromPathAsync(audioUrl.base_url);
                        _ffmpegMSSAudio = await FFmpegInteropMSS.CreateFromStreamAsync(await audioFile.OpenAsync(FileAccessMode.Read), _ffmpegConfig);
                    }
                }
                else
                {
                    _ffmpegMSSVideo = await FFmpegInteropMSS.CreateFromUriAsync(videoUrl.base_url, _ffmpegConfig);
                    if (audioUrl != null)
                    {
                        _ffmpegMSSAudio = await FFmpegInteropMSS.CreateFromUriAsync(audioUrl.base_url, _ffmpegConfig);
                    }
                   
                }


                //设置时长
                Duration = _ffmpegMSSVideo.Duration.TotalSeconds;
                //设置视频
                _playerVideo = new MediaPlayer();
                _playerVideo.Source = _ffmpegMSSVideo.CreateMediaPlaybackItem();
                //设置音频
                if (audioUrl != null)
                {
                    _playerAudio = new MediaPlayer();
                    _playerAudio.Source = _ffmpegMSSAudio.CreateMediaPlaybackItem();
                }
               
                //设置时间线控制器
                _mediaTimelineController = new MediaTimelineController();
                _playerVideo.CommandManager.IsEnabled = false;
                _playerVideo.TimelineController = _mediaTimelineController;
                if (audioUrl != null)
                {
                    _playerAudio.CommandManager.IsEnabled = false;
                    _playerAudio.TimelineController = _mediaTimelineController;
                }
              
                _playerVideo.MediaOpened += new TypedEventHandler<MediaPlayer, object>(async (e, arg) =>
                {
                    Opening = false;
                    await this.Dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Normal, () =>
                    {
                        PlayMediaOpened?.Invoke(this, new EventArgs());
                    });
                });
                //播放完成
                _playerVideo.MediaEnded += new TypedEventHandler<MediaPlayer, object>(async (e, arg) =>
                {
                    await this.Dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Normal, () =>
                    {
                        //加个判断，是否真的播放完成了
                        if (Position.ToInt32() >= Duration.ToInt32())
                        {
                            PlayState = PlayState.End;
                            Position = 0;
                            PlayStateChanged?.Invoke(this, PlayState);
                            PlayMediaEnded?.Invoke(this, new EventArgs());
                        }
                    });
                });
                //播放错误
                _playerVideo.MediaFailed += new TypedEventHandler<MediaPlayer, MediaPlayerFailedEventArgs>(async (e, arg) =>
                {
                    if (_playerVideo == null || _playerVideo.Source == null) return;
                    await this.Dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Normal, () =>
                    {
                        PlayState = PlayState.Error;
                        PlayStateChanged?.Invoke(this, PlayState);
                        ChangeEngine?.Invoke(this, new ChangePlayerEngine()
                        {
                            need_change = false,
                            message = arg.ErrorMessage
                        });
                    });

                });
                //缓冲开始
                _playerVideo.PlaybackSession.BufferingStarted += new TypedEventHandler<MediaPlaybackSession, object>(async (e, arg) =>
                {
                    await this.Dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Normal, () =>
                    {
                        Buffering = true;
                    });


                });
               
              

                //缓冲进行中
                _playerVideo.PlaybackSession.BufferingProgressChanged += new TypedEventHandler<MediaPlaybackSession, object>(async (e, arg) =>
                {
                    await this.Dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Normal, () =>
                    {
                        Buffering = true;
                        BufferCache = e.BufferingProgress;
                    });

                });
               
                //缓冲进行中
                _playerVideo.PlaybackSession.BufferingEnded += new TypedEventHandler<MediaPlaybackSession, object>(async (e, arg) =>
                {
                    await this.Dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Normal, () =>
                    {
                        Buffering = false;
                    });
                });
               
                //进度变更
                _playerVideo.PlaybackSession.PositionChanged += new TypedEventHandler<MediaPlaybackSession, object>(async (e, arg) =>
                {
                    await this.Dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Normal, () =>
                    {
                        try
                        {
                            Position = e.Position.TotalSeconds;
                        }
                        catch (Exception)
                        {
                        }
                    });
                });
                if (audioUrl != null)
                {
                    //播放错误
                    _playerAudio.MediaFailed += new TypedEventHandler<MediaPlayer, MediaPlayerFailedEventArgs>(async (e, arg) =>
                    {
                        if (_playerAudio == null || _playerAudio.Source == null) return;
                        await this.Dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Normal, () =>
                        {
                            PlayState = PlayState.Error;
                            PlayStateChanged?.Invoke(this, PlayState);
                            ChangeEngine?.Invoke(this, new ChangePlayerEngine()
                            {
                                need_change = false,
                                message = arg.Error + " " + arg.ErrorMessage + " " + (arg.ExtendedErrorCode?.ToString() ?? "")
                            });
                        });

                    });
                    _playerAudio.PlaybackSession.BufferingStarted += new TypedEventHandler<MediaPlaybackSession, object>(async (e, arg) =>
                    {
                        await this.Dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Normal, () =>
                        {
                            Buffering = true;
                        });
                    });
                    _playerAudio.PlaybackSession.BufferingProgressChanged += new TypedEventHandler<MediaPlaybackSession, object>(async (e, arg) =>
                    {
                        await this.Dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Normal, () =>
                        {
                            Buffering = true;
                            BufferCache = e.BufferingProgress;
                        });
                    });
                    _playerAudio.PlaybackSession.BufferingEnded += new TypedEventHandler<MediaPlaybackSession, object>(async (e, arg) =>
                    {
                        await this.Dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Normal, () =>
                        {
                            Buffering = false;
                        });
                    });
                    _playerAudio.MediaEnded += new TypedEventHandler<MediaPlayer, object>(async (e, arg) =>
                    {
                        await this.Dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Normal, () =>
                        {
                            if (this.PlayState != PlayState.End 
                                && _playerVideo.PlaybackSession.PlaybackState == MediaPlaybackState.Playing
                                && _playerVideo.PlaybackSession.NaturalDuration.TotalSeconds - _playerVideo.PlaybackSession.Position.TotalSeconds > 5)
                            {
                                if (isLocal)
                                {
                                    Utils.ShowMessageToast("音频过早结束，可能损坏，请尝试重新缓存");
                                }
                                else
                                {
                                    Utils.ShowMessageToast("音频过早结束，可能加载失败，正在重试");
                                    ChangeEngine?.Invoke(this, new ChangePlayerEngine()
                                    {
                                        change_engine = PlayEngine.FFmpegInteropMSS,
                                        current_mode = PlayEngine.FFmpegInteropMSS,
                                        need_change = true,
                                        play_type = PlayMediaType.Dash
                                    });
                                }
                            }
                        });
                    });
                    //设置音量
                    _playerAudio.Volume = Volume;
                    mediaPlayerAudio.SetMediaPlayer(_playerAudio);
                }
                else
                {
                    _playerVideo.Volume = Volume;
                }
                PlayState = PlayState.Pause;
                PlayStateChanged?.Invoke(this, PlayState);
                
                //绑定MediaPlayer
                mediaPlayerVideo.SetMediaPlayer(_playerVideo);
               
                //设置速率
                _mediaTimelineController.ClockRate = Rate;

                return new PlayerOpenResult()
                {
                    result = true
                };
            }
            catch (Exception ex)
            {
                //PlayMediaError?.Invoke(this, "视频加载时出错:" + ex.Message);
                return new PlayerOpenResult()
                {
                    result = false,
                    message = ex.Message,
                    detail_message = ex.StackTrace
                };
            }
        }

        /// <summary>
        /// 使用FFmpeg解码播放单FLV视频
        /// </summary>
        /// <param name="url"></param>
        /// <param name="positon"></param>
        /// <param name="needConfig"></param>
        /// <returns></returns>
        public async Task<PlayerOpenResult> PlaySingleFlvUseFFmpegInterop(string url, IDictionary<string, string> header, double positon = 0, bool needConfig = true)
        {

            try
            {
                mediaPlayerVideo.Visibility = Visibility.Visible;
                //vlcVideoView.Visibility = Visibility.Collapsed;
                Opening = true;
                current_engine = PlayEngine.FFmpegInteropMSS;

                PlayMediaType = PlayMediaType.Single;
                //加载中
                PlayState = PlayState.Loading;
                PlayStateChanged?.Invoke(this, PlayState);
                //关闭正在播放的视频
                ClosePlay();

                var _ffmpegConfig = CreateFFmpegInteropConfig(header);
                _ffmpegMSSVideo = await FFmpegInteropMSS.CreateFromUriAsync(url, _ffmpegConfig);


                //设置时长
                Duration = _ffmpegMSSVideo.Duration.TotalSeconds;
                //设置播放器
                _playerVideo = new MediaPlayer();
                var mediaSource = _ffmpegMSSVideo.CreateMediaPlaybackItem();
                _playerVideo.Source = mediaSource;

                _playerVideo.MediaOpened += new TypedEventHandler<MediaPlayer, object>(async (e, arg) =>
                {
                    Opening = false;
                    await this.Dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Normal, () =>
                    {
                        PlayMediaOpened?.Invoke(this, new EventArgs());
                    });
                });
                //播放完成
                _playerVideo.MediaEnded += new TypedEventHandler<MediaPlayer, object>(async (e, arg) =>
                {
                    await this.Dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Normal, () =>
                    {
                        //加个判断，是否真的播放完成了
                        if (Position.ToInt32() >= Duration.ToInt32())
                        {
                            PlayState = PlayState.End;
                            Position = 0;
                            PlayStateChanged?.Invoke(this, PlayState);
                            PlayMediaEnded?.Invoke(this, new EventArgs());
                        }
                    });
                });
                //播放错误
                _playerVideo.MediaFailed += new TypedEventHandler<MediaPlayer, MediaPlayerFailedEventArgs>(async (e, arg) =>
                {
                    await this.Dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Normal, () =>
                    {
                        PlayState = PlayState.Error;
                        PlayStateChanged?.Invoke(this, PlayState);
                        ChangeEngine?.Invoke(this, new ChangePlayerEngine()
                        {
                            change_engine = PlayEngine.SYEngine,
                            current_mode = PlayEngine.FFmpegInteropMSS,
                            need_change = true,
                            play_type = PlayMediaType.Single
                        });
                    });
                });
                //缓冲开始
                _playerVideo.PlaybackSession.BufferingStarted += new TypedEventHandler<MediaPlaybackSession, object>(async (e, arg) =>
                {
                    await this.Dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Normal, () =>
                    {
                        Buffering = true;
                    });
                });
                //缓冲进行中
                _playerVideo.PlaybackSession.BufferingProgressChanged += new TypedEventHandler<MediaPlaybackSession, object>(async (e, arg) =>
                {
                    await this.Dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Normal, () =>
                    {
                        Buffering = true;
                        BufferCache = e.BufferingProgress;
                    });
                });
                //缓冲进行中
                _playerVideo.PlaybackSession.BufferingEnded += new TypedEventHandler<MediaPlaybackSession, object>(async (e, arg) =>
                {
                    await this.Dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Normal, () =>
                    {
                        Buffering = false;
                    });
                });
                //进度变更
                _playerVideo.PlaybackSession.PositionChanged += new TypedEventHandler<MediaPlaybackSession, object>(async (e, arg) =>
                {
                    await this.Dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Normal, () =>
                    {
                        try
                        {
                            Position = e.Position.TotalSeconds;
                        }
                        catch (Exception)
                        {
                        }
                    });
                });

                PlayState = PlayState.Pause;
                PlayStateChanged?.Invoke(this, PlayState);
                //设置音量
                _playerVideo.Volume = Volume;
                //设置速率
                _playerVideo.PlaybackSession.PlaybackRate = Rate;
                ////设置进度
                //if (positon != 0)
                //{
                //    _playerVideo.PlaybackSession.Position = TimeSpan.FromSeconds(positon);
                //}
                //绑定MediaPlayer
                mediaPlayerVideo.SetMediaPlayer(_playerVideo);
                return new PlayerOpenResult()
                {
                    result = true
                };
            }
            catch (Exception ex)
            {
                //PlayMediaError?.Invoke(this, "视频加载时出错:" + ex.Message);
                return new PlayerOpenResult()
                {
                    result = false,
                    message = ex.Message,
                    detail_message = ex.StackTrace
                };
            }
        }
        /// <summary>
        /// 使用SYEngine解码播放FLV视频
        /// </summary>
        /// <param name="url"></param>
        /// <param name="positon"></param>
        /// <param name="needConfig"></param>
        /// <param name="epId"></param>
        /// <returns></returns>
        public async Task<PlayerOpenResult> PlaySingleFlvUseSYEngine(string url, IDictionary<string, string> header, double positon = 0, bool needConfig = true, string epId = "")
        {

            try
            {
                mediaPlayerVideo.Visibility = Visibility.Visible;
                //vlcVideoView.Visibility = Visibility.Collapsed;
                Opening = true;
                current_engine = PlayEngine.SYEngine;
                PlayMediaType = PlayMediaType.Single;
                //加载中
                PlayState = PlayState.Loading;
                PlayStateChanged?.Invoke(this, PlayState);
                //关闭正在播放的视频
                ClosePlay();
                var playList = new SYEngine.Playlist(SYEngine.PlaylistTypes.NetworkHttp);
                if (needConfig)
                {
                    playList.NetworkConfigs = CreatePlaylistNetworkConfigs(epId, header);
                }
                playList.Append(url, 0, 0);
                //设置播放器
                _playerVideo = new MediaPlayer();
                _playerVideo.Source = null;
                var mediaSource = await playList.SaveAndGetFileUriAsync();
                _playerVideo.Source = MediaSource.CreateFromUri(mediaSource);
                //设置时长
                _playerVideo.MediaOpened += new TypedEventHandler<MediaPlayer, object>(async (e, arg) =>
                {
                    Opening = false;
                    await this.Dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Normal, () =>
                    {
                        Duration = _playerVideo.PlaybackSession.NaturalDuration.TotalSeconds;
                        PlayMediaOpened?.Invoke(this, new EventArgs());

                    });
                });
                //播放完成
                _playerVideo.MediaEnded += new TypedEventHandler<MediaPlayer, object>(async (e, arg) =>
                {
                    var source = (e as MediaPlayer).Source;
                    await this.Dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Normal, () =>
                    {
                        //加个判断，是否真的播放完成了
                        if (Position.ToInt32() >= Duration.ToInt32())
                        {
                            PlayState = PlayState.End;
                            Position = 0;
                            PlayStateChanged?.Invoke(this, PlayState);
                            PlayMediaEnded?.Invoke(this, new EventArgs());
                        }
                    });
                });
                //播放错误
                _playerVideo.MediaFailed += new TypedEventHandler<MediaPlayer, MediaPlayerFailedEventArgs>(async (e, arg) =>
                {
                    await this.Dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Normal, () =>
                    {
                        PlayState = PlayState.Error;
                        PlayStateChanged?.Invoke(this, PlayState);
                        ChangeEngine?.Invoke(this, new ChangePlayerEngine()
                        {
                            need_change = false,
                            play_type = PlayMediaType.Single,
                            message = arg.ErrorMessage
                        });

                    });
                });
                //缓冲开始
                _playerVideo.PlaybackSession.BufferingStarted += new TypedEventHandler<MediaPlaybackSession, object>(async (e, arg) =>
                {
                    await this.Dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Normal, () =>
                    {
                        Buffering = true;
                    });

                });
                //缓冲进行中
                _playerVideo.PlaybackSession.BufferingProgressChanged += new TypedEventHandler<MediaPlaybackSession, object>(async (e, arg) =>
                {
                    await this.Dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Normal, () =>
                    {
                        Buffering = true;
                        BufferCache = e.BufferingProgress;
                    });
                });
                //缓冲进行中
                _playerVideo.PlaybackSession.BufferingEnded += new TypedEventHandler<MediaPlaybackSession, object>(async (e, arg) =>
                {
                    await this.Dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Normal, () =>
                    {
                        Buffering = false;
                    });
                });
                //进度变更
                _playerVideo.PlaybackSession.PositionChanged += new TypedEventHandler<MediaPlaybackSession, object>(async (e, arg) =>
                {
                    await this.Dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Normal, () =>
                    {
                        try
                        {
                            Position = e.Position.TotalSeconds;
                        }
                        catch (Exception)
                        {
                        }
                    });
                });

                PlayState = PlayState.Pause;
                PlayStateChanged?.Invoke(this, PlayState);
                //设置音量
                _playerVideo.Volume = Volume;
                //设置速率
                _playerVideo.PlaybackSession.PlaybackRate = Rate;
                //设置进度
                //if (positon != 0)
                //{
                //    _playerVideo.PlaybackSession.Position = TimeSpan.FromSeconds(positon);
                //}
                //绑定MediaPlayer
                mediaPlayerVideo.SetMediaPlayer(_playerVideo);
                return new PlayerOpenResult()
                {
                    result = true
                };
            }
            catch (Exception ex)
            {
                //PlayMediaError?.Invoke(this, "视频加载时出错:" + ex.Message);
                return new PlayerOpenResult()
                {
                    result = false,
                    message = ex.Message,
                    detail_message = ex.StackTrace
                };
            }
        }
        /// <summary>
        /// 使用SYEngine解码播放多段FLV视频
        /// </summary>
        /// <param name="url"></param>
        /// <param name="positon"></param>
        /// <param name="needConfig"></param>
        /// <param name="epId"></param>
        /// <returns></returns>
        public async Task<PlayerOpenResult> PlayVideoUseSYEngine(List<FlvDurlModel> url, IDictionary<string, string> header, double positon = 0, bool needConfig = true, string epId = "", bool isLocal = false)
        {
            current_engine = PlayEngine.SYEngine;
            PlayMediaType = PlayMediaType.MultiFlv;
            try
            {
                mediaPlayerVideo.Visibility = Visibility.Visible;
                //vlcVideoView.Visibility = Visibility.Collapsed;
                Opening = false;
                //加载中
                PlayState = PlayState.Loading;
                PlayStateChanged?.Invoke(this, PlayState);
                ClosePlay();
                var playList = new SYEngine.Playlist(SYEngine.PlaylistTypes.NetworkHttp);
                if (needConfig)
                {
                    playList.NetworkConfigs = CreatePlaylistNetworkConfigs(epId);
                }
                foreach (var item in url)
                {
                    playList.Append(item.url, 0, item.length / 1000);
                }
                //设置时长
                Duration = url.Sum(x => x.length / 1000);
                //设置播放器
                _playerVideo = new MediaPlayer();
                if (isLocal)
                {
                    MediaComposition composition = new MediaComposition();
                    foreach (var item in url)
                    {
                        var file = await StorageFile.GetFileFromPathAsync(item.url);
                        var clip = await MediaClip.CreateFromFileAsync(file);
                        composition.Clips.Add(clip);
                    }
                    _playerVideo.Source = MediaSource.CreateFromMediaStreamSource(composition.GenerateMediaStreamSource());
                }
                else
                {
                    var mediaSource = await playList.SaveAndGetFileUriAsync();
                    _playerVideo.Source = MediaSource.CreateFromUri(mediaSource);

                }
                _playerVideo.MediaOpened += new TypedEventHandler<MediaPlayer, object>(async (e, arg) =>
                {
                    Opening = false;
                    await this.Dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Normal, () =>
                    {
                        PlayMediaOpened?.Invoke(this, new EventArgs());
                    });
                });
                //播放完成
                _playerVideo.MediaEnded += new TypedEventHandler<MediaPlayer, object>(async (e, arg) =>
                {
                    await this.Dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Normal, () =>
                    {
                        //加个判断，是否真的播放完成了
                        if (Position.ToInt32() >= Duration.ToInt32())
                        {
                            PlayState = PlayState.End;
                            Position = 0;
                            PlayStateChanged?.Invoke(this, PlayState);
                            PlayMediaEnded?.Invoke(this, new EventArgs());
                        }
                    });
                });
                //播放错误
                _playerVideo.MediaFailed += new TypedEventHandler<MediaPlayer, MediaPlayerFailedEventArgs>(async (e, arg) =>
                {
                    await this.Dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Normal, () =>
                    {
                        PlayState = PlayState.Error;
                        PlayStateChanged?.Invoke(this, PlayState);
                        ChangeEngine?.Invoke(this, new ChangePlayerEngine()
                        {
                            need_change = false,
                            play_type = PlayMediaType.MultiFlv,
                            message = arg.ErrorMessage
                        });
                        //PlayMediaError?.Invoke(this, arg.ErrorMessage);
                    });
                });
                //缓冲开始
                _playerVideo.PlaybackSession.BufferingStarted += new TypedEventHandler<MediaPlaybackSession, object>(async (e, arg) =>
                {
                    await this.Dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Normal, () =>
                    {
                        Buffering = true;
                    });
                });
                //缓冲进行中
                _playerVideo.PlaybackSession.BufferingProgressChanged += new TypedEventHandler<MediaPlaybackSession, object>(async (e, arg) =>
                {
                    await this.Dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Normal, () =>
                    {
                        Buffering = true;
                        BufferCache = e.BufferingProgress;
                    });
                });
                //缓冲进行中
                _playerVideo.PlaybackSession.BufferingEnded += new TypedEventHandler<MediaPlaybackSession, object>(async (e, arg) =>
                {
                    await this.Dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Normal, () =>
                    {
                        Buffering = false;
                    });
                });
                //进度变更
                _playerVideo.PlaybackSession.PositionChanged += new TypedEventHandler<MediaPlaybackSession, object>(async (e, arg) =>
                {
                    await this.Dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Normal, () =>
                    {
                        try
                        {
                            Position = e.Position.TotalSeconds;
                        }
                        catch (Exception)
                        {
                        }
                    });
                });

                PlayState = PlayState.Pause;
                PlayStateChanged?.Invoke(this, PlayState);
                //设置音量
                _playerVideo.Volume = Volume;
                //设置速率
                _playerVideo.PlaybackSession.PlaybackRate = Rate;
                //设置进度
                //if (positon != 0)
                //{
                //    _playerVideo.PlaybackSession.Position = TimeSpan.FromSeconds(positon);
                //}
                //绑定MediaPlayer
                mediaPlayerVideo.SetMediaPlayer(_playerVideo);

                return new PlayerOpenResult()
                {
                    result = true
                };
            }
            catch (Exception ex)
            {
                return new PlayerOpenResult()
                {
                    result = false,
                    message = ex.Message,
                    detail_message = ex.StackTrace
                };
            }
        }

        public void SetRatioMode(int mode)
        {

            switch (mode)
            {
                case 0:
                    mediaPlayerVideo.Width = double.NaN;
                    mediaPlayerVideo.Height = double.NaN;
                    mediaPlayerVideo.Stretch = Stretch.Uniform;
                    break;
                case 1:
                    mediaPlayerVideo.Width = double.NaN;
                    mediaPlayerVideo.Height = double.NaN;
                    mediaPlayerVideo.Stretch = Stretch.UniformToFill;
                    break;
                case 2:
                    mediaPlayerVideo.Stretch = Stretch.Fill;
                    mediaPlayerVideo.Height = this.ActualHeight;
                    mediaPlayerVideo.Width = this.ActualHeight * 16 / 9;
                    break;
                case 3:
                    mediaPlayerVideo.Stretch = Stretch.Fill;
                    mediaPlayerVideo.Height = this.ActualHeight;
                    mediaPlayerVideo.Width = this.ActualHeight * 4 / 3;
                    break;
                default:
                    break;
            }
        }
        /// <summary>
        /// 设置进度
        /// </summary>
        public void SetPosition(double position)
        {
            if (_mediaTimelineController != null)
            {
                _mediaTimelineController.Position = TimeSpan.FromSeconds(position);
            }
            else
            {
                _playerVideo.PlaybackSession.Position = TimeSpan.FromSeconds(position);
            }

        }
        /// <summary>
        /// 暂停
        /// </summary>
        public void Pause()
        {
            try
            {
                if (_mediaTimelineController != null)
                {
                    if (_mediaTimelineController.State == MediaTimelineControllerState.Running)
                    {
                        _mediaTimelineController.Pause();
                        PlayState = PlayState.Pause;
                    }
                }
                else
                {
                    if (_playerVideo.PlaybackSession.CanPause)
                    {
                        _playerVideo.Pause();
                        PlayState = PlayState.Pause;
                    }
                }
                PlayStateChanged?.Invoke(this, PlayState);
            }
            catch (Exception ex)
            {
                LogHelper.Log("暂停出现错误", LogType.ERROR, ex);
            }

        }

        /// <summary>
        /// 播放
        /// </summary>
        public void Play()
        {
            if (Position == 0 && Duration == 0) return;
            if (_mediaTimelineController != null)
            {
                if (_mediaTimelineController.State == MediaTimelineControllerState.Paused)
                {
                    _mediaTimelineController.Resume();
                    PlayState = PlayState.Playing;
                }
                else
                {
                    _mediaTimelineController.Start();
                    PlayState = PlayState.Playing;
                }
            }

            else
            {
                _playerVideo.Play();
                PlayState = PlayState.Playing;
            }

            PlayStateChanged?.Invoke(this, PlayState);
        }
        /// <summary>
        /// 设置播放速度
        /// </summary>
        /// <param name="value"></param>
        public void SetRate(double value)
        {
            Rate = value;
            if (_mediaTimelineController != null)
            {
                _mediaTimelineController.ClockRate = value;
            }
            else
            {
                if (_playerVideo != null)
                {
                    _playerVideo.PlaybackSession.PlaybackRate = value;
                }
            }

        }
        /// <summary>
        /// 停止播放
        /// </summary>
        public void ClosePlay()
        {
            //全部设置为NULL
            if (_mediaTimelineController != null)
            {
                if (_mediaTimelineController.State == MediaTimelineControllerState.Running)
                {
                    _mediaTimelineController.Pause();
                }
                _mediaTimelineController = null;
            }
            if (mediaPlayerVideo.MediaPlayer != null)
            {
                mediaPlayerVideo.SetMediaPlayer(null);

            }
            if (mediaPlayerAudio.MediaPlayer != null)
            {
                mediaPlayerVideo.SetMediaPlayer(null);
            }
            if (_playerVideo != null)
            {
                _playerVideo.Source = null;
                _playerVideo.Dispose();
                _playerVideo = null;
            }
            if (_playerAudio != null)
            {
                _playerAudio.Source = null;
                _playerAudio.Dispose();
                _playerAudio = null;
            }
            if (_ffmpegMSSVideo != null)
            {
                _ffmpegMSSVideo.Dispose();
                _ffmpegMSSVideo = null;
            }
            if (_ffmpegMSSAudio != null)
            {
                _ffmpegMSSAudio.Dispose();
                _ffmpegMSSAudio = null;
            }
            if (_mediaPlaybackList != null)
            {
                _mediaPlaybackList.Items.Clear();
                _mediaPlaybackList = null;
            }
            if (_ffmpegMSSItems != null)
            {
                _ffmpegMSSItems.Clear();
                _ffmpegMSSItems = null;
            }


            PlayState = PlayState.End;
            //进度设置为0
            Position = 0;
            Duration = 0;
        }
        /// <summary>
        /// 设置音量
        /// </summary>
        /// <param name="volume"></param>
        public void SetVolume(double volume)
        {

            if (mediaPlayerAudio.MediaPlayer != null)
            {
                mediaPlayerAudio.MediaPlayer.Volume = volume;
            }
            if (mediaPlayerVideo.MediaPlayer != null)
            {
                mediaPlayerVideo.MediaPlayer.Volume = volume;
            }
        }

        public string GetMediaInfo()
        {
            try
            {
                var info = "";
                switch (PlayMediaType)
                {
                    case PlayMediaType.Single:
                        info += $"Type: single_video\r\n";
                        break;
                    case PlayMediaType.MultiFlv:
                        info += $"Type: multi_video\r\n";
                        break;
                    case PlayMediaType.Dash:
                        info += $"Type: dash\r\n";
                        break;
                    default:
                        break;
                }
                info += $"Engine: {current_engine.ToString()}\r\n";
                if (_ffmpegMSSVideo != null)
                {
                    info += $"Resolution: {_ffmpegMSSVideo.VideoStream.PixelHeight} x {_ffmpegMSSVideo.VideoStream.PixelWidth}\r\n";
                    info += $"Video Codec: {_ffmpegMSSVideo.VideoStream.CodecName}\r\n";
                    info += $"Video Bitrate: {_ffmpegMSSVideo.VideoStream.Bitrate}\r\n";
                    info += $"Average Frame: {((double)_ffmpegMSSVideo.VideoDescriptor.EncodingProperties.FrameRate.Numerator / _ffmpegMSSVideo.VideoDescriptor.EncodingProperties.FrameRate.Denominator).ToString("0.0")}\r\n";
                    if (PlayMediaType == PlayMediaType.Dash)
                    {
                        info += $"Audio Codec: {_ffmpegMSSAudio.AudioStreams[0].CodecName}\r\n";
                        info += $"Audio Bitrate: {_ffmpegMSSAudio.AudioStreams[0].Bitrate}";
                    }
                    else
                    {
                        info += $"Audio Codec: {_ffmpegMSSVideo.AudioStreams[0].CodecName}\r\n";
                        info += $"Audio Bitrate: {_ffmpegMSSVideo.AudioStreams[0].Bitrate}";
                    }
                }
                else
                {
                    //info += $"Resolution: {_playerVideo.PlaybackSession.NaturalVideoHeight} x {_playerVideo.PlaybackSession.NaturalVideoWidth}\r\n";
                    if (_dash_video != null && _dash_audio != null)
                    {
                        info += $"Resolution: {_dash_video.width} x {_dash_video.height}\r\n";
                        info += $"Video Codec: {_dash_video.codecs}\r\n";
                        info += $"Video DataRate: {(_dash_video.bandwidth / 1024).ToString("0.0")}Kbps\r\n";
                        info += $"Average Frame: {_dash_video.fps}\r\n";
                        info += $"Audio Codec: {_dash_audio.codecs}\r\n";
                        info += $"Audio DataRate: {(_dash_audio.bandwidth / 1024).ToString("0.0")}Kbps\r\n";
                    }
                    else
                    {
                        info += $"Resolution: {_playerVideo.PlaybackSession.NaturalVideoWidth} x {_playerVideo.PlaybackSession.NaturalVideoHeight}\r\n";
                    }
                }
                //MediaInfo = info;
                return info;
            }
            catch (Exception)
            {
                //MediaInfo = "读取失败";
                return "读取视频信息失败";
            }

        }

        public void Dispose()
        {
            this.ClosePlay();
            //try
            //{
            //    _vlcMediaPlayer?.Media?.Dispose();

            //    _vlcMediaPlayer?.Dispose();

            //    _vlcMediaPlayer=null;
            //    _libVLC?.Dispose();
            //    _libVLC = null;
            //}
            //catch (Exception)
            //{
            //}

        }
        private async Task<AdaptiveMediaSource> CreateAdaptiveMediaSource(DashItemModel video, DashItemModel audio, IDictionary<string, string> httpHeader)
        {
            try
            {
                HttpClient httpClient = new HttpClient();
                if (httpHeader != null)
                {
                    foreach (var item in httpHeader)
                    {
                        httpClient.DefaultRequestHeaders.Add(item.Key, item.Value);
                    }
                }
                var mpdStr = "";
                if (audio != null)
                {
                    mpdStr = $@"<MPD xmlns=""urn:mpeg:DASH:schema:MPD:2011""  profiles=""urn:mpeg:dash:profile:isoff-on-demand:2011"" type=""static"">
                  <Period  start=""PT0S"">
                    <AdaptationSet>
                      <ContentComponent contentType=""video"" id=""1"" />
                      <Representation bandwidth=""{video.bandwidth}"" codecs=""{video.codecs}"" height=""{video.height}"" id=""{video.id}"" mimeType=""{video.mimeType}"" width=""{video.width}"">
                        <BaseURL></BaseURL>
                        <SegmentBase indexRange=""{video.SegmentBase.indexRange}"">
                          <Initialization range=""{video.SegmentBase.Initialization}"" />
                        </SegmentBase>
                      </Representation>
                    </AdaptationSet>
                    <AdaptationSet>
                      <ContentComponent contentType=""audio"" id=""2"" />
                      <Representation bandwidth=""{audio.bandwidth}"" codecs=""{audio.codecs}"" id=""{audio.id}"" mimeType=""{audio.mimeType}"" >
                        <BaseURL></BaseURL>
                        <SegmentBase indexRange=""{audio.SegmentBase.indexRange}"">
                          <Initialization range=""{audio.SegmentBase.Initialization}"" />
                        </SegmentBase>
                      </Representation>
                    </AdaptationSet>
                  </Period>
                </MPD>";
                }
                else
                {
                    mpdStr = $@"<MPD xmlns=""urn:mpeg:DASH:schema:MPD:2011""  profiles=""urn:mpeg:dash:profile:isoff-on-demand:2011"" type=""static"">
                  <Period  start=""PT0S"">
                    <AdaptationSet>
                      <ContentComponent contentType=""video"" id=""1"" />
                      <Representation bandwidth=""{video.bandwidth}"" codecs=""{video.codecs}"" height=""{video.height}"" id=""{video.id}"" mimeType=""{video.mimeType}"" width=""{video.width}"">
                        <BaseURL></BaseURL>
                        <SegmentBase indexRange=""{video.SegmentBase.indexRange}"">
                          <Initialization range=""{video.SegmentBase.Initialization}"" />
                        </SegmentBase>
                      </Representation>
                    </AdaptationSet>
                  </Period>
                </MPD>";
                }



                var stream = new MemoryStream(Encoding.UTF8.GetBytes(mpdStr)).AsInputStream();
                var soure = await AdaptiveMediaSource.CreateFromStreamAsync(stream, new Uri(video.baseUrl), "application/dash+xml", httpClient);
                var s = soure.Status;
                soure.MediaSource.DownloadRequested += (sender, args) =>
                {
                    if (args.ResourceContentType == "audio/mp4")
                    {
                        args.Result.ResourceUri = new Uri(audio.baseUrl);
                    }
                };
                return soure.MediaSource;
            }
            catch (Exception)
            {
                return null;
            }

        }
        private FFmpegInteropConfig CreateFFmpegInteropConfig(IDictionary<string, string> httpHeader)
        {

            var passthrough = SettingHelper.GetValue<bool>(SettingHelper.Player.HARDWARE_DECODING, true);
            var _ffmpegConfig = new FFmpegInteropConfig();
            if (httpHeader != null && httpHeader.ContainsKey("User-Agent"))
            {
                _ffmpegConfig.FFmpegOptions.Add("user_agent", httpHeader["User-Agent"]);
            }
            if (httpHeader != null && httpHeader.ContainsKey("Referer"))
            {
                _ffmpegConfig.FFmpegOptions.Add("referer", httpHeader["Referer"]);
            }
            _ffmpegConfig.PassthroughVideoHEVC = passthrough;
            _ffmpegConfig.PassthroughVideoH264 = passthrough;
            _ffmpegConfig.VideoDecoderMode = passthrough ? VideoDecoderMode.ForceSystemDecoder : VideoDecoderMode.ForceFFmpegSoftwareDecoder;
            return _ffmpegConfig;
        }

        private SYEngine.PlaylistNetworkConfigs CreatePlaylistNetworkConfigs(string epId = "", IDictionary<string, string> httpHeader = null)
        {

            SYEngine.PlaylistNetworkConfigs config = new SYEngine.PlaylistNetworkConfigs();
            config.DownloadRetryOnFail = true;
            config.HttpCookie = string.Empty;
            config.UniqueId = string.Empty;
            if (httpHeader != null && httpHeader.ContainsKey("User-Agent"))
            {
                config.HttpUserAgent = "Mozilla/5.0 (Windows NT 10.0; WOW64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/69.0.3497.100 Safari/537.36";
            }
            if (httpHeader != null && httpHeader.ContainsKey("Referer"))
            {
                config.HttpReferer = string.IsNullOrEmpty(epId) ? httpHeader["Referer"] : "https://www.bilibili.com/bangumi/play/ep" + epId;
            }
            return config;
        }

        //private void vlcVideoView_Initialized(object sender, LibVLCSharp.Platforms.UWP.InitializedEventArgs e)
        //{
        //    //_libVLC = new LibVLCSharp.Shared.LibVLC(enableDebugLogs: true, e.SwapChainOptions);
        //    ////LibVLC.SetUserAgent("Mozilla/5.0", "Mozilla/5.0 (Windows NT 10.0; WOW64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/86.0.4240.198 Safari/537.36");

        //    //_libVLC.Log += LibVLC_Log;
        //    //_vlcMediaPlayer = new LibVLCSharp.Shared.MediaPlayer(_libVLC);
        //    //vlcVideoView.MediaPlayer = _vlcMediaPlayer;
        //}

        //private void LibVLC_Log(object sender, LibVLCSharp.Shared.LogEventArgs e)
        //{
        //    Debug.WriteLine(e.FormattedLog);
        //}
    }

    public class PlayerOpenResult
    {
        public bool result { get; set; }
        public string message { get; set; }
        public string detail_message { get; set; }
    }
    public class ChangePlayerEngine
    {
        public bool need_change { get; set; }
        /// <summary>
        /// 当前引擎
        /// </summary>
        public PlayEngine current_mode { get; set; }
        /// <summary>
        /// 更换引擎
        /// </summary>
        public PlayEngine change_engine { get; set; }

        public PlayMediaType play_type { get; set; }
        public string message { get; set; }
    }

}
