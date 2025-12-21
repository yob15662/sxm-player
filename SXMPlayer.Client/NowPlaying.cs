using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace SXMPlayer;
public record NowPlayingData(string channel, string artist, string song, string? id);
