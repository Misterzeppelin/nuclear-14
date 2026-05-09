using System.Linq;
using Content.Goobstation.Common.DeviceNetwork;
using Content.Goobstation.Shared.StationRadio.Components;
using Content.Server.DeviceNetwork;
using Content.Server.DeviceNetwork.Components;
using Content.Server.DeviceNetwork.Systems;
using Content.Server.Radio;
using Content.Server.Radio.Components;
using Content.Shared.DeviceLinking;
using Content.Shared.DeviceLinking.Events;
using Content.Shared.DeviceNetwork;
using Content.Shared.Power;
using Robust.Shared.Audio;
using Robust.Shared.Audio.Components;
using Robust.Shared.Audio.Systems;
using Robust.Shared.Timing;

namespace Content.Server._Goobstation.StationRadio.Systems;

public sealed class StationRadioSystem : EntitySystem
{
    // Vinyl -> Server -> Receivers commands
    public const string PlayAudioCommand = "station_radio_play_audio";
    public const string StopAudioCommand = "station_radio_stop_audio";
    public const string SetAudioStateCommand = "station_radio_set_audio_state";

    // Request that is sent backwards from a receiver to vinyl player to get info about the audio.
    // In a perfect world this wouldn't
    public const string AudioRequestCommand = "station_radio_request_audio";
    public const string AudioPathData = "station_radio_data_audio_path";
    public const string AudioPlaybackData = "station_radio_data_audio_playback";
    public const string AudioStateData = "station_radio_data_audio_state";

    [Dependency] private readonly SharedAudioSystem _audio = default!;
    [Dependency] private readonly DeviceNetworkSystem _device = default!;
    [Dependency] private readonly IGameTiming _timing = default!;

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<StationRadioServerComponent, NewLinkEvent>(OnServerNewLink);
        SubscribeLocalEvent<StationRadioServerComponent, DeviceNetworkPacketEvent>(OnServerRelay);
        SubscribeLocalEvent<StationRadioServerComponent, DeviceNetworkFrequencyChangedEvent>(OnServerChangeFrequency);
        SubscribeLocalEvent<StationRadioServerComponent, PowerChangedEvent>(OnServerPowerChaned);
        SubscribeLocalEvent<StationRadioServerComponent, EntityTerminatingEvent>(OnServerTerminating);

        SubscribeLocalEvent<StationRadioReceiverComponent, ToggleRadioSpeakerEvent>(OnToggleRadioSpeaker);
        SubscribeLocalEvent<StationRadioReceiverComponent, DeviceNetworkPacketEvent>(OnReceive);
        SubscribeLocalEvent<StationRadioReceiverComponent, DeviceNetworkFrequencyChangedEvent>(OnReceiverChangeFrequency);
        SubscribeLocalEvent<StationRadioReceiverComponent, PowerChangedEvent>(OnReceiverPowerChanged);

        SubscribeLocalEvent<RadioRigComponent, NewLinkEvent>(OnRigNewLink);
    }

    #region Server
    private void OnServerNewLink(Entity<StationRadioServerComponent> ent, ref NewLinkEvent args)
    {
        if (args.SourcePort != ent.Comp.MusicOutputPort)
            return;

        ent.Comp.VinylPlayer = args.Source;
    }

    private void OnServerRelay(Entity<StationRadioServerComponent> ent, ref DeviceNetworkPacketEvent args)
    {
        if (!TryComp<DeviceNetworkComponent>(ent.Owner, out var network)) return;
        if (args.Data.TryGetValue(DeviceNetworkConstants.Command, out string? command))
        {
            switch (command)
            {
                case PlayAudioCommand:
                    if (args.Data.TryGetValue(AudioPathData, out SoundSpecifier? sound)
                    && args.Data.TryGetValue(AudioPlaybackData, out float playback))
                    {
                        var startPayload = new NetworkPayload()
                        {
                            [DeviceNetworkConstants.Command] = PlayAudioCommand,
                            [AudioPathData] = sound,
                            [AudioPlaybackData] = playback
                        };
                        _device.QueuePacket(ent.Owner, null, startPayload, network.ReceiveFrequency);
                    }
                    break;
                case StopAudioCommand:
                    var stopPayload = new NetworkPayload()
                    {
                        [DeviceNetworkConstants.Command] = StopAudioCommand
                    };
                    _device.QueuePacket(ent.Owner, null, stopPayload, args.Frequency);
                    break;
                case AudioRequestCommand:
                    if (ent.Comp.VinylPlayer == null || !TryComp<VinylPlayerComponent>(ent.Comp.VinylPlayer, out var vinyl))
                        break;
                    if (vinyl.SoundEntity == null || !TryComp<AudioComponent>(vinyl.SoundEntity, out var audio))
                        break;

                    var currentSong = new SoundPathSpecifier(audio.FileName, audio.Params);
                    var position = CalculateAudioPosition(audio);
                    var startPayload2 = new NetworkPayload()
                    {
                        [DeviceNetworkConstants.Command] = PlayAudioCommand,
                        [AudioPathData] = currentSong,
                        [AudioPlaybackData] = position
                    };
                    _device.QueuePacket(ent.Owner, args.SenderAddress, startPayload2, args.Frequency);
                    break;
            }
        }
    }

    private void OnServerChangeFrequency(Entity<StationRadioServerComponent> ent, ref DeviceNetworkFrequencyChangedEvent args)
    {
        var query = EntityQueryEnumerator<StationRadioServerComponent, DeviceNetworkComponent>();
        while (query.MoveNext(out var uid, out var server, out var serverNetwork))
        {
            if (serverNetwork.ReceiveFrequency == args.NewFrequency)
            {
                args.Cancelled = true;
                return;
            }
        }

        if (TryComp<RadioMicrophoneComponent>(ent.Owner, out var radioMic) && args.NewFrequency != null)
        {
            radioMic.Frequency = (int) args.NewFrequency;
        }

        if (TryComp<DeviceLinkSinkComponent>(ent.Owner, out var link))
        {
            var rig = link.LinkedSources.FirstOrDefault(HasComp<RadioRigComponent>);
            if (rig == default)
                return;

            if (!TryComp<RadioMicrophoneComponent>(rig, out var microphone) || args.NewFrequency == null)
                return;
            microphone.Frequency = (int) args.NewFrequency;
        }

        // Tell all old listeners to stop playing
        var payload = new NetworkPayload
        {
            [DeviceNetworkConstants.Command] = StopAudioCommand
        };
        _device.QueuePacket(ent.Owner, null, payload, args.OldFrequency);

        // Tell all new listeners to start playing
        if (ent.Comp.VinylPlayer == null || !TryComp<VinylPlayerComponent>(ent.Comp.VinylPlayer, out var vinyl))
            return;
        if (!TryComp<AudioComponent>(vinyl.SoundEntity, out var audio))
            return;

        var currentSong = new SoundPathSpecifier(audio.FileName, audio.Params);
        var position = CalculateAudioPosition(audio);
        var startPayload = new NetworkPayload()
        {
            [DeviceNetworkConstants.Command] = PlayAudioCommand,
            [AudioPathData] = currentSong,
            [AudioPlaybackData] = position
        };
        _device.QueuePacket(ent.Owner, null, startPayload, args.NewFrequency);
    }

    private void OnServerPowerChaned(Entity<StationRadioServerComponent> ent, ref PowerChangedEvent args)
    {
        if (!TryComp<DeviceNetworkComponent>(ent.Owner, out var network)) return;

        if (!args.Powered)
        {
            var payload = new NetworkPayload
            {
                [DeviceNetworkConstants.Command] = StopAudioCommand
            };
            _device.QueuePacket(ent.Owner, null, payload, network.ReceiveFrequency);
        }
        else
        {
            if (ent.Comp.VinylPlayer == null || !TryComp<VinylPlayerComponent>(ent.Comp.VinylPlayer, out var vinyl))
                return;
            if (!TryComp<AudioComponent>(vinyl.SoundEntity, out var audio))
                return;

            var currentSong = new SoundPathSpecifier(audio.FileName, audio.Params);
            var position = CalculateAudioPosition(audio);
            var startPayload = new NetworkPayload()
            {
                [DeviceNetworkConstants.Command] = PlayAudioCommand,
                [AudioPathData] = currentSong,
                [AudioPlaybackData] = position
            };
            _device.QueuePacket(ent.Owner, null, startPayload, network.ReceiveFrequency);
        }
    }

    private void OnServerTerminating(Entity<StationRadioServerComponent> ent, ref EntityTerminatingEvent args)
    {
        if (!TryComp<DeviceNetworkComponent>(ent.Owner, out var network) 
            || network.ReceiveFrequency == null)
            return;

        var query = EntityQueryEnumerator<StationRadioReceiverComponent, DeviceNetworkComponent>();
        while (query.MoveNext(out var uid, out var receiver, out var receiverNetwork))
        {
            if (receiverNetwork.ReceiveFrequency != network.ReceiveFrequency)
                continue;

            receiver.SoundEntity = _audio.Stop(receiver.SoundEntity);
        }
    }


    #endregion
    #region Receiver

    private void OnToggleRadioSpeaker(Entity<StationRadioReceiverComponent> ent, ref ToggleRadioSpeakerEvent args)
    {
        if (!TryComp<DeviceNetworkComponent>(ent.Owner, out var network))
            return;
        if (!args.Enabled)
        {
            ent.Comp.SoundEntity = _audio.Stop(ent.Comp.SoundEntity);
        }
        else
        {
            var payload = new NetworkPayload
            {
                [DeviceNetworkConstants.Command] = AudioRequestCommand
            };
            _device.QueuePacket(ent.Owner, null, payload, network.ReceiveFrequency);
        }
    }

    private void OnReceive(Entity<StationRadioReceiverComponent> ent, ref DeviceNetworkPacketEvent args)
    {
        if (!TryComp<DeviceNetworkComponent>(ent.Owner, out var network))
            return;

        if (args.Address != null && args.Address != network.Address)
            return;

        if (!args.Data.TryGetValue(DeviceNetworkConstants.Command, out string? command))
            return;

        switch (command)
        {
            case PlayAudioCommand:
                if (args.Data.TryGetValue(AudioPathData, out SoundSpecifier? sound)
                && args.Data.TryGetValue(AudioPlaybackData, out float playback))
                    PlayAudio(ent, sound, playback);
                break;
            case StopAudioCommand:
                ent.Comp.SoundEntity = _audio.Stop(ent.Comp.SoundEntity);
                break;
            case SetAudioStateCommand:
                if (args.Data.TryGetValue(AudioStateData, out AudioState state))
                    _audio.SetState(ent.Comp.SoundEntity, state);
                break;
        }
    }

    private void OnReceiverChangeFrequency(Entity<StationRadioReceiverComponent> ent, ref DeviceNetworkFrequencyChangedEvent args)
    {
        if (TryComp<RadioMicrophoneComponent>(ent.Owner, out var radioMic) && args.NewFrequency != null)
        {
            radioMic.Frequency = (int) args.NewFrequency;
        }

        // Send a request to get currently playing music and it's playback position
        ent.Comp.SoundEntity = _audio.Stop(ent.Comp.SoundEntity);
        var payload = new NetworkPayload
        {
            [DeviceNetworkConstants.Command] = AudioRequestCommand
        };
        _device.QueuePacket(ent.Owner, null, payload, args.NewFrequency);
    }

    private void OnReceiverPowerChanged(Entity<StationRadioReceiverComponent> ent, ref PowerChangedEvent args)
    {
        if (!TryComp<DeviceNetworkComponent>(ent.Owner, out var network) || !TryComp<RadioSpeakerComponent>(ent.Owner, out var speaker))
            return;

        if (!args.Powered)
        {
            ent.Comp.SoundEntity = _audio.Stop(ent.Comp.SoundEntity);
        }
        else
        {
            if (!speaker.Enabled) return;

            var payload = new NetworkPayload
            {
                [DeviceNetworkConstants.Command] = AudioRequestCommand
            };
            _device.QueuePacket(ent.Owner, null, payload, network.ReceiveFrequency);
        }
    }

    #endregion

    #region Radio Rig
    private void OnRigNewLink(Entity<RadioRigComponent> ent, ref NewLinkEvent args)
    {
        if (args.SourcePort != ent.Comp.MicrophoneOutputPort)
            return;

        if (!TryComp<RadioMicrophoneComponent>(ent.Owner, out var microphone) || !TryComp<DeviceNetworkComponent>(args.Sink, out var network) || network.ReceiveFrequency == null)
            return;
        microphone.Frequency = (int) network.ReceiveFrequency;
    }

    #endregion

    private void PlayAudio(Entity<StationRadioReceiverComponent> ent, SoundSpecifier? sound, float playback = 0)
    {
        // Remove the previous audio entity if it existed
        if (ent.Comp.SoundEntity != null)
            ent.Comp.SoundEntity = _audio.Stop(ent.Comp.SoundEntity);

        var audio = _audio.PlayPvs(sound,
            ent.Owner,
            ent.Comp.DefaultParams);
        if (audio != null)
            ent.Comp.SoundEntity = audio.Value.Entity;

        _audio.SetPlaybackPosition(audio, playback);
    }

    // Why is this not exposed in the Audio API robust tools ugh
    private float CalculateAudioPosition(AudioComponent audio, float? length = null, float? position = null)
    {
        position ??= (float) ((audio.PauseTime ?? _timing.CurTime) - audio.AudioStart).TotalSeconds;
        length ??= (float) _audio.GetAudioLength(audio.FileName).TotalSeconds;

        if (audio.Params.Loop)
            position %= length;

        var maxOffset = Math.Max((float) length - 0.01f, 0f);
        position = Math.Clamp(position.Value, 0f, maxOffset);

        return position.Value;
    }
}
