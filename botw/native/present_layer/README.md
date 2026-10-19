# present_layer

A tiny Vulkan layer that writes one CLOCK_MONOTONIC timestamp per `vkQueuePresentKHR` to `$PRESENT_LOG`.
Used to measure Ryubing/BOTW-PC and other emulators (Eden) with the same method (`docs/BACKEND_COMPARISON.md`).

```bash
gcc -O2 -shared -fPIC -fvisibility=hidden -I<vulkan-headers>/include -o libbotwpc_present_log.so present_layer.c -lpthread
VK_LAYER_PATH=$PWD VK_INSTANCE_LAYERS=VK_LAYER_BOTWPC_present_log PRESENT_LOG=/tmp/presents.txt <program>
```
Nothing is installed system-wide.
