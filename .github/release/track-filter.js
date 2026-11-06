// A release track: semantic-release that looks only at the commits that concern one product of this repository.
//
// Yaparr is one repository with two things that are released on their own: the computer app (MSI, .deb, Flatpak) and the Android app (APK).
// Each has its own version and its own tags. Without this, a commit that only changes the Android app would make a new computer release, and
// the other way round. The track decides which commits count by the files each one changed:
//
//   android  the Android app, the library and the shared layer it is built from, and its page in the documentation
//   desktop  everything except what is only for the Android app
//
// A commit that changes both counts for both. A commit that counts for neither (a change to the website, say) makes no release.
//
// It stands in for the commit analyser and the release notes generator, which it runs on the commits that count. Use it first in "plugins":
//   ["./.github/release/track-filter.js", { "track": "android" }]

const { execFileSync } = require('node:child_process');
const commitAnalyzer = require('@semantic-release/commit-analyzer');
const notesGenerator = require('@semantic-release/release-notes-generator');

// Only for the Android app.
const ANDROID_ONLY = [/^apps\/Yaparr\.Android\//, /^docs\/android-app\.md$/];
// What the Android app is built from, besides its own folder.
const ANDROID_USES = [/^src\/Yaparr\//, /^src\/Yaparr\.AppCore\//, /^Directory\.Build\.props$/, /^\.github\/release\/android\.json$/];

function filesOf(hash) {
  // -m: for a merge commit, the files against each parent; --root: a first commit has no parent.
  return execFileSync('git', ['diff-tree', '--no-commit-id', '--name-only', '-r', '-m', '--root', hash], { encoding: 'utf8' })
    .split('\n')
    .map(line => line.trim())
    .filter(Boolean);
}

function concerns(track, files) {
  const androidOnly = file => ANDROID_ONLY.some(pattern => pattern.test(file));
  if (track === 'android') return files.some(file => androidOnly(file) || ANDROID_USES.some(pattern => pattern.test(file)));
  if (track === 'desktop') return files.some(file => !androidOnly(file));
  throw new Error(`Unknown release track "${track}"; use "android" or "desktop".`);
}

function onlyThisTrack(pluginConfig, context) {
  const commits = context.commits.filter(commit => concerns(pluginConfig.track, filesOf(commit.hash)));
  context.logger.log(`Release track "${pluginConfig.track}": ${commits.length} of ${context.commits.length} commits concern it.`);
  return { ...context, commits };
}

module.exports = {
  analyzeCommits: (pluginConfig, context) => commitAnalyzer.analyzeCommits(pluginConfig, onlyThisTrack(pluginConfig, context)),
  generateNotes: (pluginConfig, context) => notesGenerator.generateNotes(pluginConfig, onlyThisTrack(pluginConfig, context)),
  // Exported for the test below.
  concerns,
};
