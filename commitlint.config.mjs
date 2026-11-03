// Commit messages follow Conventional Commits: https://www.conventionalcommits.org
//   <type>(<optional scope>): <subject in lower case>
//
//   feat(core): add hysteresis to conditions
//   fix(http): dispose the response when IsFailure throws
//   docs: explain approvals in the healing plans page
//
// Checked locally by the commit-msg hook (npm install enables it) and in CI on every pull request.
export default {
  extends: ['@commitlint/config-conventional'],
  // Dependabot writes its subjects with a capital letter ("build(deps): Bump x from 1.0 to 1.1", "ci(deps): Bump the x group
  // with 2 updates"): accept those messages as they are, here and on main after a squash merge.
  ignores: [(message) => /^(build|ci)(\(deps(-dev)?\))?: Bump (\S+ from \S+ to \S+|the \S+ group)/.test(message)],
  rules: {
    // Optional, but when present it must name a part of the repository.
    'scope-enum': [
      2,
      'always',
      [
        'core', 'analyzers', 'artemis', 'aspire', 'aspnetcore', 'azure', 'caching', 'cli', 'data', 'efcore', 'featureflags', 'grpc', 'healthchecks',
        'http', 'kafka', 'kubernetes', 'mongodb', 'polly', 'rabbitmq', 'redis', 'servicebus', 'templates', 'testing', 'samples',
        'docs', 'deps', 'ci', 'release',
      ],
    ],
    'header-max-length': [2, 'always', 100],
    // Long lines in bodies (links, Dependabot release notes) are reported but do not fail.
    'body-max-line-length': [1, 'always', 100],
    // Trailers such as Co-Authored-By or links to sessions may be long.
    'footer-max-line-length': [0],
  },
};
