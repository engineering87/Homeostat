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
  rules: {
    // Optional, but when present it must name a part of the repository.
    'scope-enum': [
      2,
      'always',
      ['core', 'aspnetcore', 'healthchecks', 'http', 'polly', 'testing', 'samples', 'docs', 'deps', 'ci', 'release'],
    ],
    'header-max-length': [2, 'always', 100],
    // Long lines in bodies (links, Dependabot release notes) are reported but do not fail.
    'body-max-line-length': [1, 'always', 100],
    // Trailers such as Co-Authored-By or links to sessions may be long.
    'footer-max-line-length': [0],
  },
};
