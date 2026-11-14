using System.Runtime.InteropServices;

namespace StopTheClip.Structures
{
    //----
    // The one field of the game's DrawObject this plugin writes: the draw
    // flags byte (FFXIVClientStructs DrawObject.Flags). Only ever cast a
    // pointer that is known to be a DrawObject to this.
    //----
    [StructLayout(LayoutKind.Explicit)]
    public unsafe struct Model
    {
        [FieldOffset(0x88)] public ModelCullTypes CullType;
    }
}
