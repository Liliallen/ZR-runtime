using Ryujinx.Graphics.GAL.Multithreading.Model;
using System;

namespace Ryujinx.Graphics.GAL.Multithreading.Commands
{
    struct SetBotwCameraCommand : IGALCommand, IGALCommand<SetBotwCameraCommand>
    {
        public readonly CommandType CommandType => CommandType.SetBotwCamera;
        private SpanRef<float> _data;

        public void Set(SpanRef<float> data)
        {
            _data = data;
        }

        public static void Run(ref SetBotwCameraCommand command, ThreadedRenderer threaded, IRenderer renderer)
        {
            ReadOnlySpan<float> data = command._data.Get(threaded);
            renderer.Pipeline.SetBotwCamera(data);
            command._data.Dispose(threaded);
        }
    }
}
