import { Title } from "@mantine/core";
import classes from "./SubmissionPage.module.css"
import { useTranslation } from "react-i18next";

interface Model {
    /* ... */
}

const data: Model = { /* ... */};

export default function SubmissionPage() {
    const { t } = useTranslation();
    return (
        <>
            <Title>{t("Submission")}</Title>
        </>
    );
}
