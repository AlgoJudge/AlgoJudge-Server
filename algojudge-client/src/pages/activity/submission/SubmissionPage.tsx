import { Button, Group, Title } from "@mantine/core";
import classes from "./SubmissionPage.module.css"
import { useTranslation } from "react-i18next";
import { Link, useNavigate, useParams } from "react-router-dom";

interface Model {
    /* ... */
}

const data: Model = { /* ... */ };

export default function SubmissionPage() {
    const { t } = useTranslation();
    const navigate = useNavigate();
    const params = useParams();
    return (
        <>
            <Title>{t("Submission")}</Title>
            <Group justify="space-between">
                <Button onClick={() => navigate(-1)}>{t("Back")}</Button>
                <Button component={Link} to={`/activity/${params.activityId}/submission/${params.submissionId}/code`}>{t("Source code")}</Button>
            </Group>
        </>
    );
}
