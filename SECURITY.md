# Security Policy

## Reporting a Vulnerability

Researchers can report vulnerabilities directly to security **at** freeboardhq.com for coordinated, non-public disclosure.

Freeboard, and it's maintainer organisation Ologist Ltd, endeavors to acknowledge and fix any reported vulnerabilities ASAP. Acknowledgement is typically within 1 business day, and patches usually go out within 5 business days (depending on severity and timing).

### Scope

In scope:
- Freeboard product source code: [github.com/ologistio/freeboard](https://github.com/ologistio/freeboard)
- Freeboard REST API documentation: [freeboardhq.com/docs/rest-api/rest-api](https://freeboardhq.com/docs/rest-api/rest-api)

Out of scope:
- Marketing pages, blogs, and landing pages on freeboardhq.com
- Third-party hosted services (unless they directly impact a primary in-scope asset)
- Physical offices and infrastructure
- Employee social media accounts

Reports that are typically not eligible:
- Missing HTTP security headers (unless they lead to a proven, demonstrated vulnerability)
- Theoretical vulnerabilities without proof of exploitation
- Automated tool output without clear impact evidence
- Self-XSS requiring significant user interaction
- Issues solely affecting outdated browsers

### Vulnerability tracking

GitHub issues concerning vulnerabilities will be tagged with the **security** label to differentiate them from other issues and maintain SOC2 compliance.  

### Compatibility

Freeboard reserves the right to make breaking changes for security. Security fixes may introduce backward-incompatible changes and may be released in minor or patch versions.
