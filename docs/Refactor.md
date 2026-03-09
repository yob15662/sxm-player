Suggested split (high impact)

1.	Authentication/session
[x] Move LoginIfNecessary(string), InitializeActivityTimer(), status checks into SxmSessionService.
2.	Channel state
•	Move _currentChannel, _allChannels, GetChannelsAsync(), SetCurrentChannel(string), GetCurrentChannel() into ChannelStateService.
3.	Playlist building
•	Move most of GetStreamPlaylist(string, SXMListener?, string?, bool, int) logic (line parsing, #EXTINF, URL rewriting, cache string) into PlaylistComposer.
•	Keep only coordination in SiriusXMPlayer.
4.	Now playing resolution
•	Move RefreshAllCuts(string), GetNowPlaying(), SetNowPlayingFromSegment(SXMSegment, bool) into MetadataService.
5.	Listener/client tracking
•	Move TrackListenerIP(IPAddress), UpdateClientActivity(), SetPrimaryClient(SXMListener?), EnsurePrimaryExists(SXMListener?), HasActiveClient() into ListenerRegistry.
6.	Segment retrieval/cache
•	Move GetSegmentInternal(string, string, string, SXMListener?), HTTP fetch + cache handling into SegmentService.
7.	Streaming pipeline
•	Keep StreamIcecastAsync(string, HttpContext, CancellationToken) thin: delegate producer/consumer details to icecastStreamer + HlsSegmentProducer.