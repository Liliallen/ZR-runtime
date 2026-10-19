#import <Foundation/Foundation.h>

// Settings of the iPad app (NSUserDefaults), applied to the runtime's Config.json before a boot.
@interface GameSettings : NSObject
@property (class, nonatomic) NSInteger frameRate;      // 30, 60, 90, 120
@property (class, nonatomic) double resolutionScale;   // 0.5 .. 2 (1 = native: 720p handheld, 1080p docked)
@property (class, nonatomic) BOOL docked;
@property (class, nonatomic) BOOL showStats;
@property (class, nonatomic) double touchOpacity;      // 0 hides the touch controls
@property (class, nonatomic) BOOL xboxPrompts;         // in-game button prompts show the Xbox label of each button
@end

// Files of the app: Documents/BOTW-PC (the runtime's data folder: keys, firmware, saves, Config.json) and
// Documents/Games (base game, update and DLC files). Both can be filled from the Files app or Finder.
@interface GameData : NSObject
+ (NSString*)documents;
+ (NSString*)dataDir;
+ (NSString*)gamesDir;
// Points the data folder's game, update and DLC paths at Documents/Games (the container path changes on reinstall,
// and folders copied from a Mac carry Mac paths), applies GameSettings to Config.json. Returns the base game path, or
// nil with *error set.
+ (NSString*)prepare:(NSString**)error;
// Copies the saves (bis/user/save) to Documents/SaveBackups/<date>; returns the backup folder or nil.
+ (NSString*)backupSaves;
+ (NSArray<NSString*>*)saveBackups;
// Restores a backup at the next boot, before the game starts (the current saves are backed up first).
+ (void)requestRestore:(NSString*)backup;
@end
