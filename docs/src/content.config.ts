import { defineCollection } from 'astro:content';
import { docsLoader } from '@astrojs/starlight/loaders';
import { docsSchema } from '@astrojs/starlight/schema';
import { ExtendDocsSchema } from 'lucode-starlight/schema';
import { z } from 'astro/zod';

const LearningDocsSchema = ExtendDocsSchema.extend({
    docType: z.enum(['lesson', 'experiment', 'guide', 'concept', 'reference', 'runbook', 'maintainer']).optional(),
    audience: z.array(z.string()).optional(),
    maturity: z.enum(['planned', 'partial', 'experimental', 'stable', 'deprecated']).optional(),
    verifiedOn: z.coerce.date().optional(),
    datadogDocs: z.string().url().optional(),
    learning: z
        .object({
            objectives: z.array(z.string()).min(1).optional(),
            prerequisites: z.array(z.string()).optional(),
            estimatedMinutes: z.number().int().positive().optional(),
        })
        .optional(),
});

export const collections = {
    docs: defineCollection({
        loader: docsLoader(),
        schema: docsSchema({ extend: LearningDocsSchema }),
    }),
};
