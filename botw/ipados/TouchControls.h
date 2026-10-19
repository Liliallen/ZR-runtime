#import <UIKit/UIKit.h>

// On-screen controller: two floating sticks (left/right halves), face buttons, shoulders, D-pad, +/-. Sends the state
// to the runtime (botwpc_touch) on every change.
@interface TouchControls : UIView
@property (nonatomic, copy) void (^onSettings)(void);
@end
