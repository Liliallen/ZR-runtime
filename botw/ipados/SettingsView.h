#import <UIKit/UIKit.h>

// In-game settings (gear button, or L3 + R3 on a controller): frame rate (live), resolution and docked mode (next
// boot), statistics, touch-control opacity, save backup/restore, quit.
@interface SettingsView : UIView
@property (nonatomic, copy) void (^onClose)(void);
@property (nonatomic, copy) void (^onChange)(void);
@end
