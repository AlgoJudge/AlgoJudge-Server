import { Title } from "@mantine/core";
import classes from "./QuestionsPage.module.css"
import { useTranslation } from "react-i18next";

interface Model {
    /* ... */
}

const data: Model = { /* ... */};

export default function QuestionsPage() {
    const { t } = useTranslation();
    return (
        <>
            <Title>{t("Questions and announcements")}</Title>
        </>
    );
}
