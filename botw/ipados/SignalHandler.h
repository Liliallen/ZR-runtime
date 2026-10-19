#pragma once
#include <signal.h>

// For botwpc_set_signal_handler(botw_signal_handler, botw_signal_set_config).
void botw_signal_handler(int sig, siginfo_t* info, void* ucontext);
void botw_signal_set_config(void* config);
