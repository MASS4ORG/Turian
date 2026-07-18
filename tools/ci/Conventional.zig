/// Conventional-commit classification shared by version bumping and the
/// CHANGELOG generator. Type keywords match case-insensitively so squash-merge
/// titles like "Feat: …" still classify.
const std = @import("std");

pub const BumpKind = enum { none, patch, minor, major };

/// A CHANGELOG section and the semver bump its commit types imply.
pub const Section = struct {
    label: []const u8,
    keywords: []const []const u8,
    bump: BumpKind,
};

pub const sections = [_]Section{
    .{ .label = "Features", .keywords = &.{"feat"}, .bump = .minor },
    .{ .label = "Bug Fixes", .keywords = &.{"fix"}, .bump = .patch },
    .{ .label = "Performance", .keywords = &.{"perf"}, .bump = .patch },
    .{ .label = "Other", .keywords = &.{ "chore", "ci", "docs", "refactor", "style", "test", "build" }, .bump = .patch },
};

/// Highest semver bump implied by a set of commit subjects.
pub fn determineBump(commits: []const []const u8) BumpKind {
    var result: BumpKind = .none;
    for (commits) |c| {
        if (isBreaking(c)) return .major;
        for (sections) |sec| {
            if (matchesKeywords(c, sec.keywords)) {
                if (sec.bump == .minor and result != .major) result = .minor;
                if (sec.bump == .patch and result == .none) result = .patch;
                break;
            }
        }
    }
    return result;
}

/// True when the subject carries a breaking-change marker: "!" before the
/// first colon, or a BREAKING CHANGE / BREAKING-CHANGE token.
pub fn isBreaking(c: []const u8) bool {
    if (std.mem.indexOf(u8, c, "BREAKING CHANGE") != null) return true;
    if (std.mem.indexOf(u8, c, "BREAKING-CHANGE") != null) return true;
    const colon = std.mem.indexOf(u8, c, ":") orelse return false;
    return colon > 0 and c[colon - 1] == '!';
}

/// Case-insensitive prefix match of a commit subject against section keywords
/// ("feat:", "Feat(scope):", "feat!:").
pub fn matchesKeywords(s: []const u8, keywords: []const []const u8) bool {
    for (keywords) |kw| {
        if (std.ascii.startsWithIgnoreCase(s, kw)) {
            const rest = s[kw.len..];
            if (rest.len > 0 and (rest[0] == ':' or rest[0] == '(' or rest[0] == '!')) return true;
        }
    }
    return false;
}

/// True when the subject is breaking or matches some section.
pub fn isClassified(c: []const u8) bool {
    if (isBreaking(c)) return true;
    for (sections) |sec| {
        if (matchesKeywords(c, sec.keywords)) return true;
    }
    return false;
}

test "breaking markers force a major bump" {
    try std.testing.expectEqual(BumpKind.major, determineBump(&.{"feat!: new mesh format"}));
    try std.testing.expectEqual(BumpKind.major, determineBump(&.{"Feat!: new mesh format"}));
    try std.testing.expectEqual(BumpKind.major, determineBump(&.{"feat(mesh)!: new format"}));
    try std.testing.expectEqual(BumpKind.major, determineBump(&.{"fix: x BREAKING CHANGE: y"}));
    try std.testing.expectEqual(BumpKind.major, determineBump(&.{ "fix: a", "feat!: b" }));
}

test "commit types map to bumps case-insensitively" {
    try std.testing.expectEqual(BumpKind.minor, determineBump(&.{"feat: add thing"}));
    try std.testing.expectEqual(BumpKind.minor, determineBump(&.{"Feat: multi mesh models #45"}));
    try std.testing.expectEqual(BumpKind.patch, determineBump(&.{"fix: bug"}));
    try std.testing.expectEqual(BumpKind.patch, determineBump(&.{"chore: tidy"}));
    try std.testing.expectEqual(BumpKind.minor, determineBump(&.{ "fix: bug", "feat: thing" }));
}

test "unclassified subjects imply no bump" {
    try std.testing.expectEqual(BumpKind.none, determineBump(&.{"Merge branch 'x' into 'main'"}));
    try std.testing.expectEqual(BumpKind.none, determineBump(&.{"feature: not a type"}));
    try std.testing.expect(!isClassified("Merge branch 'x'"));
    try std.testing.expect(isClassified("Feat: multi mesh models #45"));
}
