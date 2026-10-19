#import "TouchControls.h"
#import "BotwPc.h"

typedef NS_ENUM(NSInteger, Role) { RoleNone, RoleLeftStick, RoleRightStick, RoleButton };

@interface Control : NSObject
@property (nonatomic) int button;   // BotwPc* (0 = settings)
@property (nonatomic) CGPoint center;
@property (nonatomic) CGFloat radius;
@property (nonatomic, strong) CAShapeLayer* layer;
@property (nonatomic, strong) CATextLayer* label;
@end
@implementation Control
@end

@implementation TouchControls {
    NSMutableArray<Control*>* _controls;
    NSMapTable<UITouch*, NSNumber*>* _roles;       // touch -> Role (or button index + RoleButton * 1000)
    CGPoint _leftOrigin, _rightOrigin, _left, _right;
    CAShapeLayer *_leftBase, *_leftKnob, *_rightBase, *_rightKnob;
    uint64_t _buttons;
}

static const CGFloat StickRadius = 70;

- (instancetype)initWithFrame:(CGRect)frame {
    if ((self = [super initWithFrame:frame])) {
        self.multipleTouchEnabled = YES;
        self.backgroundColor = UIColor.clearColor;
        _controls = [NSMutableArray array];
        _roles = [NSMapTable strongToStrongObjectsMapTable];
        _leftBase = [self circleLayer:StickRadius filled:NO];
        _leftKnob = [self circleLayer:28 filled:YES];
        _rightBase = [self circleLayer:StickRadius filled:NO];
        _rightKnob = [self circleLayer:28 filled:YES];
        for (CAShapeLayer* l in @[_leftBase, _leftKnob, _rightBase, _rightKnob]) { l.hidden = YES; [self.layer addSublayer:l]; }
    }
    return self;
}

- (CAShapeLayer*)circleLayer:(CGFloat)r filled:(BOOL)filled {
    CAShapeLayer* l = [CAShapeLayer layer];
    l.path = [UIBezierPath bezierPathWithOvalInRect:CGRectMake(-r, -r, 2 * r, 2 * r)].CGPath;
    l.fillColor = filled ? [UIColor colorWithWhite:1 alpha:0.35].CGColor : [UIColor colorWithWhite:0 alpha:0.15].CGColor;
    l.strokeColor = [UIColor colorWithWhite:1 alpha:0.6].CGColor;
    l.lineWidth = 2;
    return l;
}

- (void)addButton:(int)button title:(NSString*)title at:(CGPoint)center radius:(CGFloat)radius {
    Control* c = [Control new];
    c.button = button;
    c.center = center;
    c.radius = radius;
    c.layer = [self circleLayer:radius filled:NO];
    c.layer.position = center;
    c.label = [CATextLayer layer];
    c.label.string = title;
    c.label.fontSize = radius > 30 ? 20 : 15;
    c.label.alignmentMode = kCAAlignmentCenter;
    c.label.foregroundColor = UIColor.whiteColor.CGColor;
    c.label.contentsScale = UIScreen.mainScreen.scale;
    c.label.frame = CGRectMake(center.x - radius, center.y - c.label.fontSize * 0.65, 2 * radius, c.label.fontSize * 1.4);
    [self.layer addSublayer:c.layer];
    [self.layer addSublayer:c.label];
    [_controls addObject:c];
}

- (void)layoutSubviews {
    [super layoutSubviews];
    for (Control* c in _controls) { [c.layer removeFromSuperlayer]; [c.label removeFromSuperlayer]; }
    [_controls removeAllObjects];
    CGFloat w = self.bounds.size.width, h = self.bounds.size.height;
    // Face buttons (Switch layout: A right, B bottom, X top, Y left).
    CGPoint face = CGPointMake(w - 120, h - 190);
    [self addButton:BotwPcA title:@"A" at:CGPointMake(face.x + 52, face.y) radius:32];
    [self addButton:BotwPcB title:@"B" at:CGPointMake(face.x, face.y + 52) radius:32];
    [self addButton:BotwPcX title:@"X" at:CGPointMake(face.x, face.y - 52) radius:32];
    [self addButton:BotwPcY title:@"Y" at:CGPointMake(face.x - 52, face.y) radius:32];
    // Shoulders.
    [self addButton:BotwPcZL title:@"ZL" at:CGPointMake(60, 70) radius:34];
    [self addButton:BotwPcL title:@"L" at:CGPointMake(140, 70) radius:30];
    [self addButton:BotwPcR title:@"R" at:CGPointMake(w - 140, 70) radius:30];
    [self addButton:BotwPcZR title:@"ZR" at:CGPointMake(w - 60, 70) radius:34];
    // D-pad, +/-, stick clicks.
    CGPoint pad = CGPointMake(120, h - 300);
    [self addButton:BotwPcUp title:@"▲" at:CGPointMake(pad.x, pad.y - 44) radius:24];
    [self addButton:BotwPcDown title:@"▼" at:CGPointMake(pad.x, pad.y + 44) radius:24];
    [self addButton:BotwPcLeft title:@"◀" at:CGPointMake(pad.x - 44, pad.y) radius:24];
    [self addButton:BotwPcRight title:@"▶" at:CGPointMake(pad.x + 44, pad.y) radius:24];
    [self addButton:BotwPcMinus title:@"−" at:CGPointMake(w / 2 - 70, 40) radius:22];
    [self addButton:BotwPcPlus title:@"+" at:CGPointMake(w / 2 + 70, 40) radius:22];
    [self addButton:BotwPcLStick title:@"L3" at:CGPointMake(250, h - 60) radius:24];
    [self addButton:BotwPcRStick title:@"R3" at:CGPointMake(w - 250, h - 60) radius:24];
    [self addButton:0 title:@"⚙︎" at:CGPointMake(w / 2, 40) radius:22];
}

- (Control*)controlAt:(CGPoint)p {
    for (Control* c in _controls) {
        CGFloat dx = p.x - c.center.x, dy = p.y - c.center.y;
        if (dx * dx + dy * dy <= (c.radius + 10) * (c.radius + 10)) return c;
    }
    return nil;
}

- (void)setPressed:(Control*)c down:(BOOL)down {
    if (c.button == 0) {
        if (down && self.onSettings) self.onSettings();
        return;
    }
    if (down) _buttons |= 1ull << c.button; else _buttons &= ~(1ull << c.button);
    c.layer.fillColor = down ? [UIColor colorWithWhite:1 alpha:0.45].CGColor : [UIColor colorWithWhite:0 alpha:0.15].CGColor;
}

static CGPoint StickValue(CGPoint origin, CGPoint p) {
    CGFloat dx = (p.x - origin.x) / StickRadius, dy = (origin.y - p.y) / StickRadius; // +Y up
    CGFloat len = sqrt(dx * dx + dy * dy);
    if (len > 1) { dx /= len; dy /= len; }
    return CGPointMake(dx, dy);
}

- (void)send {
    botwpc_touch(_buttons, _left.x, _left.y, _right.x, _right.y);
}

- (void)showStick:(BOOL)left origin:(CGPoint)o value:(CGPoint)v {
    [CATransaction begin];
    [CATransaction setDisableActions:YES];
    CAShapeLayer* base = left ? _leftBase : _rightBase;
    CAShapeLayer* knob = left ? _leftKnob : _rightKnob;
    base.hidden = knob.hidden = NO;
    base.position = o;
    knob.position = CGPointMake(o.x + v.x * StickRadius, o.y - v.y * StickRadius);
    [CATransaction commit];
}

- (void)touchesBegan:(NSSet<UITouch*>*)touches withEvent:(UIEvent*)event {
    for (UITouch* t in touches) {
        CGPoint p = [t locationInView:self];
        Control* c = [self controlAt:p];
        if (c) {
            [_roles setObject:@(RoleButton * 1000 + [_controls indexOfObject:c]) forKey:t];
            [self setPressed:c down:YES];
        } else if (p.x < self.bounds.size.width / 2) {
            [_roles setObject:@(RoleLeftStick) forKey:t];
            _leftOrigin = p; _left = CGPointZero;
            [self showStick:YES origin:p value:_left];
        } else {
            [_roles setObject:@(RoleRightStick) forKey:t];
            _rightOrigin = p; _right = CGPointZero;
            [self showStick:NO origin:p value:_right];
        }
    }
    [self send];
}

- (void)touchesMoved:(NSSet<UITouch*>*)touches withEvent:(UIEvent*)event {
    for (UITouch* t in touches) {
        NSInteger role = [[_roles objectForKey:t] integerValue];
        CGPoint p = [t locationInView:self];
        if (role == RoleLeftStick) { _left = StickValue(_leftOrigin, p); [self showStick:YES origin:_leftOrigin value:_left]; }
        else if (role == RoleRightStick) { _right = StickValue(_rightOrigin, p); [self showStick:NO origin:_rightOrigin value:_right]; }
    }
    [self send];
}

- (void)touchesEnded:(NSSet<UITouch*>*)touches withEvent:(UIEvent*)event {
    for (UITouch* t in touches) {
        NSInteger role = [[_roles objectForKey:t] integerValue];
        if (role == RoleLeftStick) { _left = CGPointZero; _leftBase.hidden = _leftKnob.hidden = YES; }
        else if (role == RoleRightStick) { _right = CGPointZero; _rightBase.hidden = _rightKnob.hidden = YES; }
        else if (role >= RoleButton * 1000) [self setPressed:_controls[role - RoleButton * 1000] down:NO];
        [_roles removeObjectForKey:t];
    }
    [self send];
}

- (void)touchesCancelled:(NSSet<UITouch*>*)touches withEvent:(UIEvent*)event {
    [self touchesEnded:touches withEvent:event];
}
@end
