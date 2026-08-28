#!/usr/bin/env node

import { readFileSync, readdirSync } from 'node:fs';
import { join, relative } from 'node:path';
import { fileURLToPath } from 'node:url';

const DOCS_DIR = fileURLToPath(new URL('../src/content/docs/', import.meta.url));
const files = [];

function collect(dir) {
    for (const entry of readdirSync(dir, { withFileTypes: true })) {
        const path = join(dir, entry.name);
        if (entry.isDirectory()) collect(path);
        else if (/\.mdx?$/.test(entry.name)) files.push(path);
    }
}

function frontmatterOf(source) {
    const match = source.match(/^---\n([\s\S]*?)\n---/);
    return match?.[1] ?? '';
}

function field(frontmatter, name) {
    return frontmatter.match(new RegExp(`^${name}:\\s*["']?([^\\n"']+)`, 'm'))?.[1]?.trim();
}

function proseOnly(source) {
    return source.replace(/^---\n[\s\S]*?\n---/, '').replace(/```[\s\S]*?```/g, '').replace(/`[^`]*`/g, '');
}

collect(DOCS_DIR);

let errors = 0;
let warnings = 0;

for (const file of files) {
    const source = readFileSync(file, 'utf8');
    const frontmatter = frontmatterOf(source);
    const relativePath = relative(DOCS_DIR, file);
    const docType = field(frontmatter, 'docType');
    const maturity = field(frontmatter, 'maturity');
    const verifiedOn = field(frontmatter, 'verifiedOn');

    if (['stable', 'partial', 'experimental'].includes(maturity) && !verifiedOn) {
        console.error(`CONTENT ERROR: ${relativePath} declares maturity '${maturity}' without verifiedOn.`);
        errors++;
    }

    if (['lesson', 'experiment'].includes(docType) && !/^\s{2}objectives:/m.test(frontmatter)) {
        console.error(`CONTENT ERROR: ${relativePath} is a ${docType} without learning objectives.`);
        errors++;
    }

    const prose = proseOnly(source);
    const headingLevels = [...prose.matchAll(/^(#{2,6})\s+/gm)].map((match) => match[1].length);
    for (let index = 1; index < headingLevels.length; index++) {
        if (headingLevels[index] > headingLevels[index - 1] + 1) {
            console.warn(`CONTENT WARNING: ${relativePath} skips a heading level.`);
            warnings++;
            break;
        }
    }

    const wordCount = prose.replace(/<[^>]+>/g, ' ').match(/[\p{L}\p{N}_'-]+/gu)?.length ?? 0;
    if (wordCount > 1800 && !['reference', 'runbook', 'maintainer'].includes(docType)) {
        console.warn(`CONTENT WARNING: ${relativePath} has about ${wordCount} prose words; review its scope or classify its document type.`);
        warnings++;
    }
}

if (errors > 0) {
    console.error(`check-content: ${errors} error(s), ${warnings} warning(s) across ${files.length} pages.`);
    process.exit(1);
}

console.log(`check-content: 0 errors, ${warnings} warning(s) across ${files.length} pages.`);
