#import "GameData.h"

static NSUserDefaults* Defaults(void) { return NSUserDefaults.standardUserDefaults; }

@implementation GameSettings
+ (NSInteger)frameRate { NSInteger v = [Defaults() integerForKey:@"frameRate"]; return v ? v : 30; }
+ (void)setFrameRate:(NSInteger)v { [Defaults() setInteger:v forKey:@"frameRate"]; }
+ (double)resolutionScale { double v = [Defaults() doubleForKey:@"resolutionScale"]; return v > 0 ? v : 1.0; }
+ (void)setResolutionScale:(double)v { [Defaults() setDouble:v forKey:@"resolutionScale"]; }
+ (BOOL)docked { return [Defaults() objectForKey:@"docked"] ? [Defaults() boolForKey:@"docked"] : NO; }
+ (void)setDocked:(BOOL)v { [Defaults() setBool:v forKey:@"docked"]; }
+ (BOOL)showStats { return [Defaults() objectForKey:@"showStats"] ? [Defaults() boolForKey:@"showStats"] : YES; }
+ (void)setShowStats:(BOOL)v { [Defaults() setBool:v forKey:@"showStats"]; }
+ (double)touchOpacity { return [Defaults() objectForKey:@"touchOpacity"] ? [Defaults() doubleForKey:@"touchOpacity"] : 0.5; }
+ (void)setTouchOpacity:(double)v { [Defaults() setDouble:v forKey:@"touchOpacity"]; }
+ (BOOL)xboxPrompts { return [Defaults() objectForKey:@"xboxPrompts"] ? [Defaults() boolForKey:@"xboxPrompts"] : YES; }
+ (void)setXboxPrompts:(BOOL)v { [Defaults() setBool:v forKey:@"xboxPrompts"]; }
@end

@interface GameData ()
+ (BOOL)restoreSaves:(NSString*)backup;
@end

@implementation GameData

+ (NSString*)documents {
    return NSSearchPathForDirectoriesInDomains(NSDocumentDirectory, NSUserDomainMask, YES).firstObject;
}
+ (NSString*)dataDir { return [self.documents stringByAppendingPathComponent:@"BOTW-PC"]; }
+ (NSString*)gamesDir { return [self.documents stringByAppendingPathComponent:@"Games"]; }

static id ReadJson(NSString* path) {
    NSData* data = [NSData dataWithContentsOfFile:path];
    return data ? [NSJSONSerialization JSONObjectWithData:data options:NSJSONReadingMutableContainers error:nil] : nil;
}

static void WriteJson(id object, NSString* path) {
    NSData* data = [NSJSONSerialization dataWithJSONObject:object
                                                   options:NSJSONWritingPrettyPrinted | NSJSONWritingWithoutEscapingSlashes
                                                     error:nil];
    [data writeToFile:path atomically:YES];
}

// A path from another machine or an older container: same file name in Documents/Games, if it is there.
static NSString* Relocated(NSString* path) {
    if (![path isKindOfClass:NSString.class] || path.length == 0) return path;
    NSString* local = [GameData.gamesDir stringByAppendingPathComponent:path.lastPathComponent];
    return [NSFileManager.defaultManager fileExistsAtPath:local] ? local : path;
}

+ (NSString*)prepare:(NSString**)error {
    NSFileManager* fm = NSFileManager.defaultManager;
    [fm createDirectoryAtPath:self.gamesDir withIntermediateDirectories:YES attributes:nil error:nil];
    if (![fm fileExistsAtPath:[self.dataDir stringByAppendingPathComponent:@"system/prod.keys"]]) {
        *error = @"No BOTW-PC data folder: copy your BOTW-PC data folder (with system/prod.keys and the firmware) to "
                 @"BOTW-iPad/BOTW-PC and the game files to BOTW-iPad/Games (Files app or Finder).";
        return nil;
    }

    NSString* pending = [Defaults() stringForKey:@"pendingRestore"];
    if (pending) {
        [Defaults() removeObjectForKey:@"pendingRestore"];
        if (![self restoreSaves:pending]) {
            *error = [NSString stringWithFormat:@"Restoring the save backup %@ failed; the saves were left as they were.", pending];
            return nil;
        }
    }

    // Base game: the one recorded in BOTW-PC's settings, else the largest .nsp/.xci in Games.
    NSString* settingsPath = [self.dataDir stringByAppendingPathComponent:@"botw/settings.json"];
    NSMutableDictionary* settings = ReadJson(settingsPath) ?: [NSMutableDictionary dictionary];
    NSMutableDictionary* game = settings[@"game"] ?: [NSMutableDictionary dictionary];
    NSString* gamePath = Relocated(game[@"path"]);
    if (![fm fileExistsAtPath:gamePath ?: @""]) {
        unsigned long long best = 0;
        gamePath = nil;
        for (NSString* name in [fm contentsOfDirectoryAtPath:self.gamesDir error:nil]) {
            if (![@[@"nsp", @"xci"] containsObject:name.pathExtension.lowercaseString]) continue;
            NSString* p = [self.gamesDir stringByAppendingPathComponent:name];
            unsigned long long size = [[fm attributesOfItemAtPath:p error:nil] fileSize];
            if (size > best) { best = size; gamePath = p; }
        }
    }
    if (!gamePath) {
        *error = @"No game file: copy the Breath of the Wild base game (.nsp or .xci), its v1.6.0 update and DLC to BOTW-iPad/Games.";
        return nil;
    }
    game[@"path"] = gamePath;
    game[@"fullscreen"] = @YES;
    settings[@"game"] = game;
    // Buttons keep the Switch positions (Switch A = the right face button); the in-game prompts can show the Xbox label
    // of the button that does it.
    NSMutableDictionary* controller = settings[@"controller"] ?: [NSMutableDictionary dictionary];
    controller[@"layout"] = @"Nintendo";
    controller[@"prompts"] = GameSettings.xboxPrompts ? @"Auto" : @"Original";
    settings[@"controller"] = controller;
    WriteJson(settings, settingsPath);

    // Update and DLC lists of every title.
    NSString* titles = [self.dataDir stringByAppendingPathComponent:@"games"];
    for (NSString* title in [fm contentsOfDirectoryAtPath:titles error:nil]) {
        NSString* updatesPath = [[titles stringByAppendingPathComponent:title] stringByAppendingPathComponent:@"updates.json"];
        NSMutableDictionary* updates = ReadJson(updatesPath);
        if ([updates isKindOfClass:NSDictionary.class]) {
            if (updates[@"selected"]) updates[@"selected"] = Relocated(updates[@"selected"]);
            NSMutableArray* paths = [NSMutableArray array];
            for (NSString* p in updates[@"paths"] ?: @[]) [paths addObject:Relocated(p)];
            updates[@"paths"] = paths;
            WriteJson(updates, updatesPath);
        }
        NSString* dlcPath = [[titles stringByAppendingPathComponent:title] stringByAppendingPathComponent:@"dlc.json"];
        NSMutableArray* dlcs = ReadJson(dlcPath);
        if ([dlcs isKindOfClass:NSArray.class]) {
            for (NSMutableDictionary* dlc in dlcs) dlc[@"path"] = Relocated(dlc[@"path"]);
            WriteJson(dlcs, dlcPath);
        }
    }

    // Graphics and frame rate.
    NSString* configPath = [self.dataDir stringByAppendingPathComponent:@"Config.json"];
    NSMutableDictionary* config = ReadJson(configPath);
    if (config) {
        NSInteger fps = GameSettings.frameRate;
        config[@"docked_mode"] = @(GameSettings.docked);
        config[@"res_scale"] = @(-1);
        config[@"res_scale_custom"] = @(GameSettings.resolutionScale);
        config[@"vsync_mode"] = @(fps == 30 ? 0 : 2);  // Switch | Custom
        config[@"enable_custom_vsync_interval"] = @YES;
        config[@"custom_vsync_interval"] = @(fps * 2); // BotW presents every second VBlank
        config[@"audio_backend"] = @"SDL2";
        // FXAA and the area/FSR scaling passes run as extra compute passes; the M1's GPU is better spent on the game.
        config[@"anti_aliasing"] = [Defaults() stringForKey:@"antiAliasing"] ?: @"None";
        config[@"scaling_filter"] = [Defaults() stringForKey:@"scalingFilter"] ?: @"Bilinear";
        config[@"use_hypervisor"] = @NO;
        WriteJson(config, configPath);
    }
    return gamePath;
}

+ (NSString*)backupRoot { return [self.documents stringByAppendingPathComponent:@"SaveBackups"]; }

+ (NSString*)backupSaves {
    NSString* saves = [self.dataDir stringByAppendingPathComponent:@"bis/user/save"];
    NSDateFormatter* f = [NSDateFormatter new];
    f.dateFormat = @"yyyy-MM-dd_HH-mm-ss";
    NSString* target = [self.backupRoot stringByAppendingPathComponent:[f stringFromDate:NSDate.date]];
    [NSFileManager.defaultManager createDirectoryAtPath:self.backupRoot withIntermediateDirectories:YES attributes:nil error:nil];
    return [NSFileManager.defaultManager copyItemAtPath:saves toPath:target error:nil] ? target : nil;
}

+ (NSArray<NSString*>*)saveBackups {
    NSArray* names = [NSFileManager.defaultManager contentsOfDirectoryAtPath:self.backupRoot error:nil] ?: @[];
    return [[names sortedArrayUsingSelector:@selector(compare:)] reverseObjectEnumerator].allObjects;
}

+ (void)requestRestore:(NSString*)backup { [Defaults() setObject:backup forKey:@"pendingRestore"]; }

+ (BOOL)restoreSaves:(NSString*)backup {
    // The current saves are backed up first, so a restore can be undone.
    if (![self backupSaves]) return NO;
    NSString* saves = [self.dataDir stringByAppendingPathComponent:@"bis/user/save"];
    NSString* source = [self.backupRoot stringByAppendingPathComponent:backup];
    NSFileManager* fm = NSFileManager.defaultManager;
    NSString* old = [saves stringByAppendingString:@".old"];
    [fm removeItemAtPath:old error:nil];
    if (![fm moveItemAtPath:saves toPath:old error:nil]) return NO;
    if (![fm copyItemAtPath:source toPath:saves error:nil]) {
        [fm moveItemAtPath:old toPath:saves error:nil];
        return NO;
    }
    [fm removeItemAtPath:old error:nil];
    return YES;
}
@end
