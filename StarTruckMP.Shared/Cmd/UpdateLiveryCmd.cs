using MessagePack;

namespace StarTruckMP.Shared.Cmd
{
    [MessagePackObject]
    public class UpdateLiveryCmd
    {
        [Key(0)]
        public string Livery { get; set; } = string.Empty;
    }
}