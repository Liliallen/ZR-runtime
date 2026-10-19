#pragma once
#include <stddef.h>
#include <stdbool.h>

// Code generation on iPadOS 26 (see botw/docs/IPADOS.md): a debugger must have attached to the process (CS_DEBUGGED).
bool jit_cs_debugged(void);
// A debugger is attached right now (P_TRACED).
bool jit_traced(void);
// botwpc_set_jit_prepare callback: makes the runtime's code arena executable.
int jit_prepare(void* address, size_t size);
