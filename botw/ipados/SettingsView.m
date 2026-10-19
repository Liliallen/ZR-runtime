#import "SettingsView.h"
#import "GameData.h"
#import "BotwPc.h"

@implementation SettingsView {
    UIStackView* _stack;
    UILabel* _status;
}

static const NSInteger FrameRates[] = {30, 60, 90, 120};
static const double Scales[] = {0.5, 0.75, 1.0, 1.5};

- (instancetype)initWithFrame:(CGRect)frame {
    if ((self = [super initWithFrame:frame])) {
        self.backgroundColor = [UIColor colorWithWhite:0 alpha:0.78];
        _stack = [UIStackView new];
        _stack.axis = UILayoutConstraintAxisVertical;
        _stack.spacing = 14;
        _stack.translatesAutoresizingMaskIntoConstraints = NO;
        [self addSubview:_stack];
        [NSLayoutConstraint activateConstraints:@[
            [_stack.centerXAnchor constraintEqualToAnchor:self.centerXAnchor],
            [_stack.centerYAnchor constraintEqualToAnchor:self.centerYAnchor],
            [_stack.widthAnchor constraintEqualToConstant:520],
        ]];
        [self build];
    }
    return self;
}

- (UILabel*)label:(NSString*)text {
    UILabel* l = [UILabel new];
    l.text = text;
    l.textColor = UIColor.whiteColor;
    l.font = [UIFont systemFontOfSize:17 weight:UIFontWeightSemibold];
    return l;
}

- (void)row:(NSString*)title control:(UIView*)control {
    UIStackView* row = [[UIStackView alloc] initWithArrangedSubviews:@[[self label:title], control]];
    row.distribution = UIStackViewDistributionFillEqually;
    [_stack addArrangedSubview:row];
}

- (UIButton*)button:(NSString*)title action:(SEL)action {
    UIButton* b = [UIButton buttonWithType:UIButtonTypeSystem];
    [b setTitle:title forState:UIControlStateNormal];
    b.titleLabel.font = [UIFont systemFontOfSize:18 weight:UIFontWeightMedium];
    [b addTarget:self action:action forControlEvents:UIControlEventTouchUpInside];
    return b;
}

- (void)build {
    UILabel* title = [self label:@"BOTW-iPad"];
    title.font = [UIFont systemFontOfSize:26 weight:UIFontWeightBold];
    [_stack addArrangedSubview:title];

    UISegmentedControl* fps = [[UISegmentedControl alloc] initWithItems:@[@"30", @"60", @"90", @"120"]];
    for (int i = 0; i < 4; i++) if (FrameRates[i] == GameSettings.frameRate) fps.selectedSegmentIndex = i;
    [fps addTarget:self action:@selector(frameRateChanged:) forControlEvents:UIControlEventValueChanged];
    [self row:@"Frame rate (FPS)" control:fps];

    UISegmentedControl* res = [[UISegmentedControl alloc] initWithItems:@[@"0.5×", @"0.75×", @"1×", @"1.5×"]];
    for (int i = 0; i < 4; i++) if (Scales[i] == GameSettings.resolutionScale) res.selectedSegmentIndex = i;
    [res addTarget:self action:@selector(scaleChanged:) forControlEvents:UIControlEventValueChanged];
    [self row:@"Resolution (next boot)" control:res];

    UISwitch* docked = [UISwitch new];
    docked.on = GameSettings.docked;
    [docked addTarget:self action:@selector(dockedChanged:) forControlEvents:UIControlEventValueChanged];
    [self row:@"Docked 1080p (next boot)" control:docked];

    UISwitch* xbox = [UISwitch new];
    xbox.on = GameSettings.xboxPrompts;
    [xbox addTarget:self action:@selector(xboxChanged:) forControlEvents:UIControlEventValueChanged];
    [self row:@"Xbox button prompts in the game (next boot)" control:xbox];

    UISwitch* stats = [UISwitch new];
    stats.on = GameSettings.showStats;
    [stats addTarget:self action:@selector(statsChanged:) forControlEvents:UIControlEventValueChanged];
    [self row:@"Performance statistics" control:stats];

    UISlider* opacity = [UISlider new];
    opacity.value = GameSettings.touchOpacity;
    [opacity addTarget:self action:@selector(opacityChanged:) forControlEvents:UIControlEventValueChanged];
    [self row:@"Touch controls" control:opacity];

    UIStackView* saves = [[UIStackView alloc] initWithArrangedSubviews:@[
        [self button:@"Back up saves" action:@selector(backup)],
        [self button:@"Restore latest backup" action:@selector(restore)],
    ]];
    saves.distribution = UIStackViewDistributionFillEqually;
    [_stack addArrangedSubview:saves];

    _status = [self label:@""];
    _status.font = [UIFont systemFontOfSize:14];
    _status.numberOfLines = 0;
    [_stack addArrangedSubview:_status];

    UIStackView* bottom = [[UIStackView alloc] initWithArrangedSubviews:@[
        [self button:@"Resume" action:@selector(close)],
        [self button:@"Quit game" action:@selector(quit)],
    ]];
    bottom.distribution = UIStackViewDistributionFillEqually;
    [_stack addArrangedSubview:bottom];
}

- (void)frameRateChanged:(UISegmentedControl*)s {
    GameSettings.frameRate = FrameRates[s.selectedSegmentIndex];
    botwpc_set_frame_rate((int)GameSettings.frameRate);
}
- (void)scaleChanged:(UISegmentedControl*)s { GameSettings.resolutionScale = Scales[s.selectedSegmentIndex]; }
- (void)dockedChanged:(UISwitch*)s { GameSettings.docked = s.on; }
- (void)xboxChanged:(UISwitch*)s { GameSettings.xboxPrompts = s.on; }
- (void)statsChanged:(UISwitch*)s { GameSettings.showStats = s.on; if (self.onChange) self.onChange(); }
- (void)opacityChanged:(UISlider*)s { GameSettings.touchOpacity = s.value; if (self.onChange) self.onChange(); }

- (void)backup {
    NSString* path = [GameData backupSaves];
    _status.text = path ? [@"Saves backed up to SaveBackups/" stringByAppendingString:path.lastPathComponent] : @"Backup failed.";
}

- (void)restore {
    // Restoring under a running game would be overwritten by its next save: it applies to the next boot.
    NSString* latest = [GameData saveBackups].firstObject;
    if (!latest) { _status.text = @"No backup yet."; return; }
    [GameData requestRestore:latest];
    _status.text = [NSString stringWithFormat:@"%@ will be restored at the next launch (the current saves are backed up first). Quit to apply.", latest];
}

- (void)close { if (self.onClose) self.onClose(); }

- (void)quit {
    botwpc_stop();
    _status.text = @"Stopping…";
    dispatch_after(dispatch_time(DISPATCH_TIME_NOW, 4 * NSEC_PER_SEC), dispatch_get_main_queue(), ^{ exit(0); });
}
@end
